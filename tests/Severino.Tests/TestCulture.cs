using System.Globalization;
using System.Runtime.CompilerServices;

namespace Severino.Tests;

internal static class TestCulture
{
    /// <summary>
    /// The assertions on messages were written against the Portuguese text, so every test runs in
    /// pt-BR whatever the Windows of whoever runs them; the English text has tests of its own.
    /// </summary>
#pragma warning disable CA2255 // a test assembly is exactly where a module initializer belongs
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void UsePortuguese()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
    }
}
