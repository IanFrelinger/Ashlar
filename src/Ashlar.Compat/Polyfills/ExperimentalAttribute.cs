using System;

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Polyfill for <c>System.Diagnostics.CodeAnalysis.ExperimentalAttribute</c> (net8.0+) when targeting
    /// netstandard2.0. The C# 12 compiler recognises the attribute by its full name, so a netstandard2.0
    /// consumer of an experimental Ashlar API gets the same <c>ASHLAREXP001</c> or <c>ASHLAREXP002</c> diagnostic as a
    /// net8.0 consumer (docs/SdkCompatibilityPolicy.md, "Experimental").
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Assembly | AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct |
        AttributeTargets.Enum | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property |
        AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Interface | AttributeTargets.Delegate,
        Inherited = false)]
    internal sealed class ExperimentalAttribute : Attribute
    {
        public ExperimentalAttribute(string diagnosticId)
        {
            DiagnosticId = diagnosticId;
        }

        public string DiagnosticId { get; }

        public string? UrlFormat { get; set; }
    }
}
