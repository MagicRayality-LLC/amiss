// Unity's .NET Standard 2.0 API profile does not ship IsExternalInit, which
// init-only property accessors require. Each assembly that uses init needs
// this internal definition present.
namespace System.Runtime.CompilerServices
{
    using global::System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}