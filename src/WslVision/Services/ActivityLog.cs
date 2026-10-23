using System.Collections.ObjectModel;
using WslVision.Models;
using WslVision.Services.Docker;

namespace WslVision.Services;

public sealed record ActivityEntry(string Text, Tone Tone, DateTimeOffset Time)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm");
}

/// <summary>
/// "Atividade" em Ajustes: o que mudou nos containers (vindo do fluxo de eventos do Docker que já está
/// aberto) e as ações do app. Na tela, só as últimas entradas; no <see cref="AppLog"/>, todas.
/// </summary>
public sealed class ActivityLog
{
    private const int Max = 30;

    public ObservableCollection<ActivityEntry> Entries { get; } = [];

    public void Add(string text, Tone tone = Tone.Ok) => Insert(new ActivityEntry(text, tone, DateTimeOffset.Now));

    public void Add(ContainerEvent e)
    {
        if (DockerText.Describe(e) is { } d)
            Insert(new ActivityEntry(d.Text, d.Tone, e.Time));
    }

    private void Insert(ActivityEntry entry)
    {
        AppLog.Info("atividade", entry.Text);
        Entries.Insert(0, entry);
        while (Entries.Count > Max)
            Entries.RemoveAt(Entries.Count - 1);
    }
}
