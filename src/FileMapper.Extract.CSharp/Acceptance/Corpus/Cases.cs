// This source is parsed as data by the acceptance entrypoint. It is never executed or built as a repository project.
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
namespace Corpus;
public static class Cases
{
    public static void CommandBad(string input) { var local = input; Process.Start(local); }
    public static void CommandConstant(string input) { Process.Start("fixed-tool"); }
    public static void CommandReassigned(string input) { var local = input; local = "fixed-tool"; Process.Start(local); }
    public static void CommandBranchBad(string input, bool flag) { string local; if (flag) local = input; else local = "fixed-tool"; Process.Start(local); }
    public static void CommandBothSafe(string input, bool flag) { string local = input; if (flag) local = "one"; else local = "two"; Process.Start(local); }
    public static void CommandReturnBad(string input) { Process.Start(Identity(input)); }
    public static void CommandReturnSafe(string input) { Process.Start(Fixed(input)); }
    public static void CommandConsole() { Process.Start(Console.ReadLine()); }
    public static void CommandTernaryBad(string input, bool flag) { Process.Start(flag ? input : "fixed"); }
    public static void CommandLoopBad(string input, int count) { string value = "fixed"; while (count-- > 0) value = input; Process.Start(value); }
    public static void CommandLoopSafe(string input, int count) { string value = input; while (count-- > 0) value = "loop"; value = "fixed"; Process.Start(value); }
    public static void SqlBad(string input) { Api.Sql("SELECT * FROM records WHERE name='" + input + "'"); }
    public static void SqlSafe(string input) { Api.Sql("SELECT * FROM records WHERE id=@id"); Api.Parameter(input); }
    public static void PathBad(string input) { File.ReadAllText(Path.Combine("safe-base", input)); }
    public static void PathSafe(string input) { File.ReadAllText(Api.AllowPath(input)); }
    public static void PathCanonicalBad(string input) { File.ReadAllText(Path.GetFullPath(input)); }
    public static void PathBarrierSafe(string input) { Api.CheckPath(input); File.ReadAllText(input); }
    public static void PathBarrierBranchBad(string input, bool flag) { if (flag) Api.CheckPath(input); File.ReadAllText(input); }
    public static void DeserializeBad(string input) { Api.Deserialize(input); }
    public static void DeserializeSafe(string input) { Api.ParseJson(input); }
    public static void NetworkBad(string input) { new HttpClient().GetStringAsync(input); }
    public static void NetworkSafe(string input) { new HttpClient().GetStringAsync("https://fixed.example"); }
    public static void SecretBad() { var secret = Environment.GetEnvironmentVariable("APP_SECRET"); Console.WriteLine(secret); }
    public static void SecretSafe() { var mode = Environment.GetEnvironmentVariable("APP_MODE"); Console.WriteLine(mode); }
    public static void SecretReassigned() { var secret = Environment.GetEnvironmentVariable("APP_SECRET"); secret = "redacted"; Console.WriteLine(secret); }
    public static void CryptoBad(string input) { MD5.HashData(Encoding.UTF8.GetBytes(input)); }
    public static void CryptoSafe(string input) { SHA256.HashData(Encoding.UTF8.GetBytes(input)); }
    public static void AuthorizationBad(string id) { Api.DeleteAccount(id); }
    public static void AuthorizationSafe(string id) { Api.RequireAuthorization(); Api.DeleteAccount(id); }
    public static void AuthorizationBranchBad(string id, bool flag) { if (flag) Api.RequireAuthorization(); Api.DeleteAccount(id); }
    public static void AuthorizationBothSafe(string id, bool flag) { if (flag) Api.RequireAuthorization(); else Api.RequireAuthorization(); Api.DeleteAccount(id); }
    public static void PropagationBad(string input) { Process.Start(Api.Wrap(input)); }
    public static void NamedArgumentsBad(string input) { Api.Run(fixedArgument: "constant", command: input); }
    public static void NamedArgumentsSafe(string input) { Api.Run(command: "fixed", fixedArgument: input); }
    public static void UnreachableSafe(string input) { if (false) Process.Start(input); }
    public static void ExceptionBad(string input) { try { Api.MayThrow(); } catch (Exception) { Process.Start(input); } }
    public static void FinallyBad(string input) { try { Api.MayThrow(); } finally { Process.Start(input); } }
    public static void LocalFunctionBad(string input) { string Local(string value) => value; Process.Start(Local(input)); }
    public static void OutParameterBad(string input) { Copy(input, out var output); Process.Start(output); }
    public static void OutParameterSafe(string input) { CopyFixed(input, out var output); Process.Start(output); }
    public static void RefParameterBad(string input) { string output = "fixed"; CopyRef(input, ref output); Process.Start(output); }
    public static void AuthorizationWrongResource(string first, string second) { Api.RequireResource(first); Api.DeleteAccount(second); }
    public static void AuthorizationResourceSafe(string first) { Api.RequireResource(first); Api.DeleteAccount(first); }
    public static void AuthorizationResourceReassigned(string first, string second) { Api.RequireResource(first); first = second; Api.DeleteAccount(first); }
    public static void AuthorizationConstantBad() { Api.DeleteAccount("fixed-account"); }
    public static void AuthorizationNoArgumentsBad() { Api.DeleteAllAccounts(); }
    public static void AuthorizationNoArgumentsSafe() { Api.RequireAuthorization(); Api.DeleteAllAccounts(); }
    public static void ProcessInfoBad(string input) { var info = new ProcessStartInfo(); info.FileName = input; Process.Start(info); }
    public static void ProcessInfoSafe(string input) { var info = new ProcessStartInfo(); info.FileName = input; info.FileName = "fixed"; Process.Start(info); }
    public static void ProcessInfoInitializerBad(string input) { var info = new ProcessStartInfo { FileName = input }; Process.Start(info); }
    public static void HeapAliasBad(string input) { var box = new Box(); var alias = box; alias.Command = input; Process.Start(box.Command); }
    public static void HeapAliasSafe(string input) { var box = new Box(); var alias = box; alias.Command = input; box.Command = "fixed"; Process.Start(alias.Command); }
    public static void FinallyMutationBad(string input) { string path = "fixed"; try { Api.MayThrow(); } finally { path = input; } File.ReadAllText(path); }
    public static void FinallyMutationSafe(string input) { string path = input; try { Api.MayThrow(); } finally { path = "fixed"; } File.ReadAllText(path); }
    public static void RequestObjectBad(string input) { var request = new HttpRequestMessage(); request.RequestUri = new Uri(input); new HttpClient().SendAsync(request); }
    public static void AuthorizationAliasSafe(string id) { var alias = id; Api.RequireResource(alias); Api.DeleteAccount(id); }
    public static void LocalCaptureBad(string input) { string Local() => input; Process.Start(Local()); }
    public static void LocalCaptureWriteBad(string input) { string command = "fixed"; void Local() { command = input; } Local(); Process.Start(command); }
    public static void LocalCaptureWriteSafe(string input) { string command = input; void Local() { command = "fixed"; } Local(); Process.Start(command); }
    private static bool Copy(string input, out string output) { output = input; return true; }
    private static bool CopyFixed(string input, out string output) { output = "fixed"; return true; }
    private static void CopyRef(string input, ref string output) { output = input; }
    private static string Identity(string input) { var local = input; return local; }
    private static string Fixed(string input) => "fixed";
    public static void Language15() { string[] values = ["one"]; System.Collections.Generic.List<string> copy = [with(capacity: 2), .. values]; }
}
public sealed class Fields
{
    private string value = "fixed";
    public void FieldBad(string input) { value = input; Process.Start(value); }
    public void FieldSafe(string input) { value = input; value = "fixed"; Process.Start(value); }
}
public sealed class Box { public string Command = "fixed"; }
// Explicitly modeled external contracts; their bodies are placeholders, not evidence of sanitization.
public static class Api
{
    public static void Sql(string text) { }
    public static void Parameter(string value) { }
    public static object Deserialize(string value) => new();
    public static object ParseJson(string value) => new();
    public static string AllowPath(string value) => value;
    public static void CheckPath(string value) { }
    public static void DeleteAccount(string id) { }
    public static void RequireAuthorization() { }
    public static void RequireResource(string resource) { }
    public static void DeleteAllAccounts() { }
    public static string Wrap(string value) => value;
    public static void Run(string command, string fixedArgument) { }
    public static void MayThrow() { }
}
