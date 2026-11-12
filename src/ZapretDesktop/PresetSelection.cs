namespace ZapretDesktop;

internal static class PresetSelection
{
    public static async Task<(Preset Preset, ProbeResult Checks)?> FindAsync(
        EngineFiles engine, EngineSession session, Targets targets, string? lastPreset,
        Action<string> status, Action<string> log, Action<ProbeResult> showChecks, CancellationToken token)
    {
        EngineSession.EnsureNoOtherEngine();
        status("Проверяем исходное соединение…");
        var baseline = await Connectivity.CheckAsync(targets, token);
        log($"Без пресета · {baseline}");
        showChecks(baseline);
        var presets = engine.Presets.OrderByDescending(preset => preset.Name == lastPreset).ToArray();

        for (int index = 0; index < presets.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var preset = presets[index];
            status($"{index + 1} из {presets.Length} · {preset.Name}");
            try { await session.StartAsync(engine, preset, token); }
            catch (InvalidOperationException error)
            {
                log($"{preset.Name} · {error.Message}");
                continue;
            }

            var checks = await Connectivity.CheckAsync(targets, token);
            if (checks.Passed && session.IsRunning)
                checks = await Connectivity.CheckAsync(targets, token);
            showChecks(checks);
            log($"{preset.Name} · {checks}");
            if (checks.Passed && session.IsRunning)
                return (preset, checks);
            session.Stop();
        }
        return null;
    }
}
