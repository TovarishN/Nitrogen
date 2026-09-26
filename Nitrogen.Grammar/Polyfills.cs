// netstandard2.0 lacks the marker type the compiler needs for records and init accessors.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
