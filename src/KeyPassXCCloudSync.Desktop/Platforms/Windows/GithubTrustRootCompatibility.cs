using System.Text;
using System.Text.Json;
using Sigstore;

namespace KeyPassXCCloudSync;

// GitHub's TUF-signed trusted root uses DNS identifiers in authority URI
// fields. Sigstore 1.1.1 expects these as absolute Uri instances, unlike
// other Sigstore implementations. This adapter accepts ONLY the two known
// GitHub identifiers, after TUF has authenticated the complete target.
internal static class GithubTrustRootCompatibility
{
    internal static TrustedRoot ParseVerifiedTarget(ReadOnlyMemory<byte> target)
    {
        using var json=JsonDocument.Parse(target);
        if (json.RootElement.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("GitHub trusted root must be an object.");
        using var buffer=new MemoryStream();
        using (var writer=new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (property.Name is not ("certificateAuthorities" or "timestampAuthorities"))
                {
                    property.WriteTo(writer);
                    continue;
                }
                if (property.Value.ValueKind!=JsonValueKind.Array)
                    throw new InvalidDataException("GitHub trusted-root authorities are invalid.");
                writer.WritePropertyName(property.Name);
                writer.WriteStartArray();
                foreach (var authority in property.Value.EnumerateArray())
                {
                    if (authority.ValueKind!=JsonValueKind.Object)
                        throw new InvalidDataException("GitHub trusted-root authority is invalid.");
                    writer.WriteStartObject();
                    var hasUri=false;
                    foreach (var field in authority.EnumerateObject())
                    {
                        if (field.Name!="uri")
                        {
                            field.WriteTo(writer);
                            continue;
                        }
                        if (hasUri || field.Value.ValueKind!=JsonValueKind.String)
                            throw new InvalidDataException("GitHub trusted-root authority URI is invalid.");
                        hasUri=true;
                        var raw=field.Value.GetString()!;
                        var normalized=raw switch
                        {
                            "fulcio.githubapp.com"=>"https://fulcio.githubapp.com",
                            "timestamp.githubapp.com"=>"https://timestamp.githubapp.com",
                            _=>raw
                        };
                        if (!Uri.TryCreate(normalized,UriKind.Absolute,out var uri) ||
                            uri.Scheme!=Uri.UriSchemeHttps || uri.UserInfo.Length!=0)
                            throw new InvalidDataException("Unexpected GitHub trusted-root authority URI.");
                        writer.WriteString("uri",normalized);
                    }
                    if (!hasUri)
                        throw new InvalidDataException("GitHub trusted-root authority URI is missing.");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return TrustedRoot.Deserialize(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
