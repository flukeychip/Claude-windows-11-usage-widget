// Polyfill required for C# 9 'init' property setters on .NET Framework 4.8
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
