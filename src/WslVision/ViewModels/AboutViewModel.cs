using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using WslVision.Services;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

/// <summary>Quem fez o app. Os dados são os públicos do site da empresa.</summary>
public static class Company
{
    public const string Name = "G3G Labs";
    public const string Tagline = "Software sob medida, aplicativos e IA";
    public const string Region = "Rio Grande do Norte, Brasil";
    public const string Cnpj = "63.109.611/0001-92";
    public const string Site = "https://www.g3glabs.com.br/";
    public const string SiteLabel = "www.g3glabs.com.br";
    public const string Email = "g3glabs@gmail.com";
    public const string Instagram = "https://www.instagram.com/g3glabs/";
    public const string LinkedIn = "https://www.linkedin.com/company/g3g-labs/";
    public const string Repository = "https://github.com/G3G-Labs/wsl-vision";
    public const string License = Repository + "/blob/main/LICENSE";
}

/// <summary>Tela Sobre: o app (versão, código, licença) e a empresa que o criou. Fica sob Ajustes, sem item próprio no menu.</summary>
public sealed partial class AboutViewModel(SettingsService settings, Notifier notifier) : PageViewModel
{
    public override string Title => "Sobre";

    public override SymbolRegular Icon => SymbolRegular.Info24;

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    public string VersionLabel => $"Versão {Version}";

    public string Copyright => $"© {DateTime.Now.Year} {Company.Name}. Distribuído sob a licença MIT.";

    [RelayCommand]
    private void Open(string url)
    {
        try
        {
            LauncherService.OpenUrl(url);
        }
        catch (Exception ex)
        {
            notifier.Error(ex);
        }
    }

    [RelayCommand]
    private void WriteEmail() => Open("mailto:" + Company.Email);

    [RelayCommand]
    private void CopyCnpj()
    {
        try
        {
            LauncherService.CopyText(Company.Cnpj);
            notifier.Show("CNPJ copiado.");
        }
        catch (Exception ex)
        {
            notifier.Error(ex);
        }
    }

    [RelayCommand]
    private void OpenSettingsFolder() => Open(settings.Folder);

    public override IEnumerable<PaletteItem> PaletteItems() =>
    [
        PaletteItem.For(PaletteSection.Actions, "Abrir o site da G3G Labs", Company.SiteLabel, SymbolRegular.Globe24, OpenCommand, Company.Site),
    ];
}
