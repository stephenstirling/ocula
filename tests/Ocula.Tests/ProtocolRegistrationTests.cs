using Microsoft.Win32;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// An unpackaged Ocula registers the ocula: protocol under HKCU\Software\Classes\ocula. These use a scratch key under
/// HKCU\Software\Ocula.Tests (removed afterwards), never the real Software\Classes.
/// </summary>
public class ProtocolRegistrationTests : IDisposable
{
    private readonly string _root = @"Software\Ocula.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _classes;

    public ProtocolRegistrationTests() => _classes = Registry.CurrentUser.CreateSubKey(_root + @"\Classes");

    public void Dispose()
    {
        _classes.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    private string? Value(string path, string? name = "") => _classes.OpenSubKey(path)?.GetValue(name) as string;

    [Fact]
    public void It_registers_the_url_protocol_with_a_command_that_passes_the_link()
    {
        ProtocolRegistration.Register(_classes, @"C:\Apps\Ocula\Ocula.exe");

        Assert.Equal("URL:Ocula", Value("ocula"));
        Assert.Equal(string.Empty, Value("ocula", "URL Protocol"));        // present, and empty: this is what makes it a URL protocol
        Assert.Equal("\"C:\\Apps\\Ocula\\Ocula.exe\" \"%1\"", Value(@"ocula\shell\open\command"));
        Assert.Equal("\"C:\\Apps\\Ocula\\Ocula.exe\",0", Value(@"ocula\DefaultIcon"));
    }

    [Fact]
    public void A_path_with_spaces_is_quoted()
    {
        ProtocolRegistration.Register(_classes, @"C:\Program Files\Ocula\Ocula.exe");

        Assert.Equal("\"C:\\Program Files\\Ocula\\Ocula.exe\" \"%1\"", Value(@"ocula\shell\open\command"));
    }

    [Fact]
    public void Registering_again_with_a_different_exe_updates_it_and_the_same_exe_changes_nothing()
    {
        ProtocolRegistration.Register(_classes, @"C:\One\Ocula.exe");
        ProtocolRegistration.Register(_classes, @"C:\One\Ocula.exe");
        Assert.Equal("\"C:\\One\\Ocula.exe\" \"%1\"", Value(@"ocula\shell\open\command"));

        ProtocolRegistration.Register(_classes, @"C:\Two\Ocula.exe");   // the build was moved
        Assert.Equal("\"C:\\Two\\Ocula.exe\" \"%1\"", Value(@"ocula\shell\open\command"));
    }

    [Fact]
    public void Other_protocols_are_left_alone()
    {
        using (var other = _classes.CreateSubKey("capta")) other.SetValue(string.Empty, "URL:Capta");

        ProtocolRegistration.Register(_classes, @"C:\Apps\Ocula\Ocula.exe");

        Assert.Equal("URL:Capta", Value("capta"));
    }

    [Fact]
    public void Only_the_scheme_ocula_is_registered()
    {
        ProtocolRegistration.Register(_classes, @"C:\Apps\Ocula\Ocula.exe");

        Assert.Equal(new[] { "ocula" }, _classes.GetSubKeyNames());
    }
}
