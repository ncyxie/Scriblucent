namespace Scriblucent.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string variable)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(variable), "1", StringComparison.Ordinal))
            Skip = $"Set {variable}=1 to run this installed-model test.";
    }
}
