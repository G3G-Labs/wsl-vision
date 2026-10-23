namespace WslVision.Models;

/// <summary>
/// Tom de um estado na interface. A cor do ponto, do selo e do halo sai daqui (Wv.Green, Wv.Amber, Wv.Red, Wv.Gray),
/// e nunca aparece sozinha: o texto do estado vai sempre junto.
/// </summary>
public enum Tone
{
    Off,
    Ok,
    Work,
    Error,
}
