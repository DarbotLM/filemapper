namespace FileMapper.Extract.PowerShell;

/// <summary>A versioned, offline catalog. Names are resolution candidates, never executable authority.</summary>
public static class PowerShellCommandCatalog
{
    public const string Version = "fm-ps-local-flow/2.1.0";
    internal sealed record Parameter(string Name, int? Position = null, bool Pipeline = false, bool Switch = false, string[]? Aliases = null);
    internal sealed record Command(string Name, Parameter[] Parameters, string? Source = null, bool PassThrough = false);
    internal static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["iex"]="Invoke-Expression", ["iwr"]="Invoke-WebRequest", ["irm"]="Invoke-RestMethod", ["saps"]="Start-Process",
        ["cat"]="Get-Content", ["gc"]="Get-Content", ["type"]="Get-Content", ["echo"]="Write-Output", ["write"]="Write-Output",
        ["%"]="ForEach-Object", ["foreach"]="ForEach-Object", ["?"]="Where-Object", ["where"]="Where-Object",
        ["sc"]="Set-Content", ["ac"]="Add-Content", ["ipmo"]="Import-Module", ["icm"]="Invoke-Command", ["sal"]="Set-Alias"
    };
    internal static readonly IReadOnlyDictionary<string, Command> Commands = Build();
    internal static readonly HashSet<string> NativeCommands = new(["cmd", "cmd.exe", "pwsh", "pwsh.exe", "powershell", "powershell.exe", "bash", "bash.exe", "sh", "sh.exe", "wscript", "wscript.exe", "cscript", "cscript.exe"], StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string,Command> Build()
    {
        Parameter P(string name,int? position=null,bool pipeline=false,bool sw=false,params string[] aliases)=>new(name,position,pipeline,sw,aliases);
        Command C(string name,Parameter[] parameters,string? source=null,bool pass=false)=>new(name,parameters,source,pass);
        var values = new[]
        {
            C("Invoke-Expression",[P("Command",0,true)]),
            C("Start-Process",[P("FilePath",0),P("ArgumentList",1,false,false,"Args"),P("WorkingDirectory"),P("Wait",sw:true),P("PassThru",sw:true)]),
            C("Invoke-Command",[P("ScriptBlock",0),P("ComputerName"),P("Session"),P("ArgumentList",null,false,false,"Args"),P("InputObject",pipeline:true),P("FilePath"),P("AsJob",sw:true)]),
            C("Invoke-WebRequest",[P("Uri",0),P("Body"),P("Headers"),P("Method"),P("OutFile"),P("UseBasicParsing",sw:true)],"network"),
            C("Invoke-RestMethod",[P("Uri",0),P("Body"),P("Headers"),P("Method")],"network"),
            C("Read-Host",[P("Prompt",0),P("AsSecureString",sw:true),P("MaskInput",sw:true)],"user-input"),
            C("Get-Content",[P("Path",0),P("LiteralPath"),P("Raw",sw:true),P("Encoding"),P("TotalCount"),P("Tail")],"file"),
            C("Get-ItemProperty",[P("Path",0),P("LiteralPath"),P("Name",1)],"registry-or-provider"),
            C("Get-ItemPropertyValue",[P("Path",0),P("LiteralPath"),P("Name",1)],"registry-or-provider"),
            C("Get-Credential",[P("Credential",0),P("UserName"),P("Message")],"secret"),
            C("Get-Secret",[P("Name",0),P("Vault"),P("AsPlainText",sw:true)],"secret"),
            C("Write-Output",[P("InputObject",0,true),P("NoEnumerate",sw:true)],pass:true),
            C("ConvertFrom-SecureString",[P("SecureString",0,true),P("AsPlainText",sw:true),P("Key")],pass:true),
            C("Set-Content",[P("Path",0),P("Value",1,true),P("LiteralPath"),P("Encoding"),P("Force",sw:true)]),
            C("Add-Content",[P("Path",0),P("Value",1,true),P("LiteralPath"),P("Encoding"),P("Force",sw:true)]),
            C("Out-File",[P("FilePath",0),P("InputObject",pipeline:true),P("Encoding"),P("Append",sw:true)]),
            C("Set-ItemProperty",[P("Path",0),P("Name",1),P("Value",2),P("LiteralPath"),P("Force",sw:true)]),
            C("New-ItemProperty",[P("Path",0),P("Name",1),P("Value",2),P("PropertyType"),P("Force",sw:true)]),
            C("Import-Module",[P("Name",0),P("FullyQualifiedName"),P("RequiredVersion"),P("MinimumVersion"),P("Force",sw:true)]),
            C("Set-Alias",[P("Name",0),P("Value",1),P("Scope"),P("Force",sw:true)]),
            C("New-Alias",[P("Name",0),P("Value",1),P("Scope"),P("Force",sw:true)]),
            C("ForEach-Object",[P("Process",0),P("Begin"),P("End"),P("InputObject",pipeline:true)]),
            C("Where-Object",[P("FilterScript",0),P("InputObject",pipeline:true)]),
            C("Get-Date",[]), C("Join-Path",[P("Path",0),P("ChildPath",1)]), C("Resolve-Path",[P("Path",0),P("LiteralPath")])
        };
        return values.ToDictionary(x=>x.Name,StringComparer.OrdinalIgnoreCase);
    }
}
