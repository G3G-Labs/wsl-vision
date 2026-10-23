using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

public class LinuxPathsTests
{
    [Theory]
    [InlineData("/home/gabriel/projetos", @"\\wsl.localhost\Debian\home\gabriel\projetos")]
    [InlineData("/", @"\\wsl.localhost\Debian\")]
    [InlineData("/home//gabriel/./x/../y", @"\\wsl.localhost\Debian\home\gabriel\y")]
    public void ToUnc(string linux, string expected) => Assert.Equal(expected, LinuxPaths.ToUnc("Debian", linux));

    [Theory]
    [InlineData(@"\\wsl.localhost\Debian\home\gabriel", "/home/gabriel")]
    [InlineData(@"\\wsl$\Debian\etc", "/etc")]
    [InlineData(@"\\wsl.localhost\Debian", "/")]
    [InlineData(@"\\WSL.LOCALHOST\debian\tmp", "/tmp")]
    public void FromUnc(string unc, string expected) => Assert.Equal(expected, LinuxPaths.FromUnc("Debian", unc));

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home")]
    [InlineData(@"\\wsl.localhost\Debian2\home")]
    [InlineData(@"C:\Users")]
    public void FromUnc_OutraDistroOuWindows_DevolveNull(string path) => Assert.Null(LinuxPaths.FromUnc("Debian", path));

    [Theory]
    [InlineData(@"C:\Users\Gabriel\Downloads\a.zip", "/mnt/c/Users/Gabriel/Downloads/a.zip")]
    [InlineData(@"D:\", "/mnt/d")]
    [InlineData(@"e:\Projetos\x y", "/mnt/e/Projetos/x y")]
    public void FromWindowsDrive(string win, string expected) => Assert.Equal(expected, LinuxPaths.FromWindowsDrive(win));

    [Theory]
    [InlineData("/home/u/proj/sub", @"..\..", @"/home/u/proj/sub/..\..")]
    [InlineData("/home/u", @"a\b.txt", @"/home/u/a\b.txt")]
    public void Combine_BarraInvertidaEhParteDoNome(string dir, string name, string expected) =>
        // No Linux, "\" é um caractere de nome comum. Tratar como separador poderia apagar a pasta errada.
        Assert.Equal(expected, LinuxPaths.Combine(dir, name));

    [Fact]
    public void FromWindowsDrive_Unc_DevolveNull() => Assert.Null(LinuxPaths.FromWindowsDrive(@"\\servidor\pasta"));

    [Theory]
    [InlineData("/home/gabriel", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/", "/")]
    [InlineData("/a/b/c/", "/a/b")]
    public void Parent(string path, string expected) => Assert.Equal(expected, LinuxPaths.Parent(path));

    [Theory]
    [InlineData("~", "/home/gabriel")]
    [InlineData("~/projetos/api", "/home/gabriel/projetos/api")]
    [InlineData("/etc", "/etc")]
    [InlineData("  ~/x  ", "/home/gabriel/x")]
    public void Expand(string path, string expected) => Assert.Equal(expected, LinuxPaths.Expand(path, "/home/gabriel"));

    [Theory]
    [InlineData("/home/gabriel", "~")]
    [InlineData("/home/gabriel/projetos", "~/projetos")]
    [InlineData("/home/gabriela", "/home/gabriela")]
    [InlineData("/etc", "/etc")]
    public void Pretty(string path, string expected) => Assert.Equal(expected, LinuxPaths.Pretty(path, "/home/gabriel"));

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/usr/")]
    [InlineData("/home/gabriel")]
    [InlineData("/home/outro")]
    [InlineData("/usr/bin")]
    [InlineData("/var/lib")]
    [InlineData("/mnt/c")]
    [InlineData("/mnt/wsl")]
    [InlineData("/mnt/c/Windows")]
    [InlineData("/mnt/c/program files")]
    [InlineData("/mnt/c/Users")]
    [InlineData("/mnt/c/users/Gabriel")]
    public void CaminhosEssenciaisSaoProtegidos(string path) => Assert.True(LinuxPaths.IsProtectedSystemPath(path, "/home/gabriel"));

    [Theory]
    [InlineData("/home/gabriel/projetos")]
    [InlineData("/etc/nginx")]
    [InlineData("/usr/local/bin")]
    [InlineData("/mnt/c/Projetos")]
    [InlineData("/mnt/c/Users/Gabriel/Downloads")]
    public void CaminhosComunsNaoSaoProtegidos(string path) => Assert.False(LinuxPaths.IsProtectedSystemPath(path, "/home/gabriel"));
}
