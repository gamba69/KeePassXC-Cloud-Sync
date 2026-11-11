using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace KeyPassXCCloudSync;

/// <summary>Single mapping of semantic actions to genuine Microsoft Fluent System Icons.
/// Uses FluentIcons.Avalonia 2.1.345 just like the supplied Card Print reference.</summary>
internal static class FluentUiIcons
{
    internal static FluentIcon Create(string key,int size,IBrush foreground)
    {
        string name=key switch
        {
            "cloud"=>"Cloud", "settings"=>"Settings", "refresh"=>"ArrowSync",
            "check"=>"CheckmarkCircle", "shield"=>"ShieldCheckmark",
            "beaker"=>"Beaker", "bell"=>"Alert", "file"=>"DocumentText",
            "history"=>"History", "plus"=>"Add", "trash"=>"Delete",
            "stop"=>"Stop", "dismiss"=>"Dismiss", "cancel"=>"DismissCircle", "folder"=>"Folder",
            "folder-open"=>"FolderOpen", "login"=>"PersonArrowRight",
            "save"=>"Save", "merge"=>"Merge", "merge-prepare"=>"DocumentArrowRight",
            "merge-confirm"=>"DocumentCheckmark", "devices"=>"Desktop",
            "play"=>"Play", "info"=>"Info", "external-link"=>"Open",
            "database"=>"Database", "warning"=>"Warning", "copy"=>"Copy",
            "edit"=>"Edit", "archive"=>"Archive", "exit"=>"SignOut",
            "clock"=>"Clock", "performance"=>"DataUsage", "arrow"=>"ArrowRight",
            _=>"Info"
        };
        // TryParse is deliberately defensive about additions/removals in the
        // upstream icon catalogue. Fall back to a genuine Fluent Info glyph.
        if(!Enum.TryParse<Icon>(name,out var symbol))symbol=Icon.Info;
        return new FluentIcon
        {
            Icon=symbol,IconSize=IconSize.Size20,Width=size,Height=size,FontSize=size,
            Foreground=foreground,HorizontalAlignment=HorizontalAlignment.Center,
            VerticalAlignment=VerticalAlignment.Center
        };
    }
    internal static string ForAction(string label)
    {
        var l=label.ToLowerInvariant();
        if(l.Contains("отмен") || l.Contains("сброс"))return "cancel";
        if(l.Contains("останов") || l.Contains("выключ"))return "stop";
        if(l.Contains("удал") || l.Contains("покинуть"))return "trash";
        if(l.Contains("добав") || l.Contains("storage"))return "plus";
        if(l.Contains("копир"))return "copy";
        if(l.Contains("отчёт") || l.Contains("журнал"))return "file";
        if(l.Contains("синхрон") || l.Contains("повтор") || l.Contains("обнов") || l.Contains("перечит"))return "refresh";
        if(l.Contains("локальн") && l.Contains("тест"))return "beaker";
        if(l.Contains("тест") || l.Contains("провер"))return "check";
        if(l.Contains("защит") || l.Contains("безопас"))return "shield";
        if(l.Contains("устрой") || l.Contains("компьютер"))return "devices";
        if(l.Contains("облак") || l.Contains("google") || l.Contains("proton") || l.Contains("webdav") || l.Contains("s3"))return "cloud";
        if(l.Contains("настро"))return "settings";
        if(l.Contains("измен") || l.Contains("переимен"))return "edit";
        if(l.Contains("истори") || l.Contains("резерв") || l.Contains("конфликт"))return "history";
        if(l.Contains("merge") || l.Contains("объедин"))return "merge";
        if(l.Contains("войти") || l.Contains("вход") || l.Contains("авториз") || l.Contains("присоедин"))return "login";
        if(l.Contains("позже"))return "clock";
        if(l.Contains("папк") || l.Contains("выбрать"))return "folder";
        if(l.Contains("сохран") || l.Contains("подтверд"))return "save";
        if(l.Contains("включ"))return "play";
        if(l.Contains("выход"))return "exit";
        return "arrow";
    }
}
