using System.Diagnostics;

namespace KeyPassXCCloudSync;

// All child-process creation, environment variables, and timeouts reside in the OS adapter.
internal sealed class WindowsCliRunner : IPlatformCliRunner
{
    public async Task<string> RunProtonAsync(string executable,int timeoutSeconds,
        CancellationToken cancellationToken,params string[] arguments)
    {
        using var process=new Process();
        process.StartInfo.FileName=executable;
        process.StartInfo.UseShellExecute=false;
        process.StartInfo.CreateNoWindow=true;
        process.StartInfo.RedirectStandardOutput=true;
        process.StartInfo.RedirectStandardError=true;
        foreach(var argument in arguments)process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["PROTON_DRIVE_LOG_LEVEL"]="ERROR";
        if(!process.Start())throw new IOException("Could not launch Proton Drive CLI");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output=await stdout;
            _=await stderr;
            if(process.ExitCode!=0)
                throw new IOException($"Proton Drive CLI operation failed (exit {process.ExitCode}); consult Proton CLI logs");
            return output;
        }
        catch
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            throw;
        }
    }
}
