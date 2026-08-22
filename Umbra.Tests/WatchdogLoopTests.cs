using Umbra.Core;

namespace Umbra.Tests;

// Couvre le verrou de confiance des plages hard mode (WatchdogLoop.Enforcer) :
// une fois qu'une plage hard mode est observée active, un periods.json
// modifié à la main (désactivée, supprimée, horaire reculé) ne doit plus
// pouvoir la débloquer avant son heure de fin légitime. Teste la méthode de
// résolution directement (internal, voir InternalsVisibleTo) plutôt que
// TickAsync en entier, qui touche le fichier hosts/pare-feu réels.
public class WatchdogLoopTests : IDisposable
{
    private readonly string _tempDir;

    public WatchdogLoopTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "umbra-tests-" + Guid.NewGuid());
        Config.DataDir = _tempDir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static WatchdogLoop.Enforcer MakeEnforcer() => new(_ => { });

    private static Period MakeHardPeriod(string id, DateTime now, int startOffsetMin, int endOffsetMin) => new()
    {
        Id = id,
        Name = "test",
        Enabled = true,
        Recurring = true,
        Days = new List<int> { (int)now.DayOfWeek },
        Date = Periods.TodayKey(now),
        StartTime = now.AddMinutes(startOffsetMin).ToString("HH:mm"),
        EndTime = now.AddMinutes(endOffsetMin).ToString("HH:mm"),
        HardMode = true,
        Apps = new List<string> { "app.exe" },
        Sites = new List<string> { "example.com" },
    };

    [Fact]
    public void ResolveHardLockedPeriods_CurrentlyActivePeriod_ReturnsNoPhantom()
    {
        var enforcer = MakeEnforcer();
        var now = DateTime.Now;
        var data = new PeriodsData { Periods = new List<Period> { MakeHardPeriod("p1", now, -10, 10) } };

        Assert.Empty(enforcer.ResolveHardLockedPeriods(data, now));
    }

    [Fact]
    public void ResolveHardLockedPeriods_DisabledWhileLocked_StaysEnforced()
    {
        var enforcer = MakeEnforcer();
        var now = DateTime.Now;
        var p = MakeHardPeriod("p1", now, -10, 10);
        enforcer.ResolveHardLockedPeriods(new PeriodsData { Periods = new List<Period> { p } }, now);

        var disabled = MakeHardPeriod("p1", now, -10, 10);
        disabled.Enabled = false; // "edition" directe : desactivee alors qu'il reste largement du temps
        var phantoms = enforcer.ResolveHardLockedPeriods(new PeriodsData { Periods = new List<Period> { disabled } }, now);
        Assert.Single(phantoms);
        Assert.Contains("app.exe", phantoms[0].Apps);
        Assert.Contains("example.com", phantoms[0].Sites);
    }

    [Fact]
    public void ResolveHardLockedPeriods_DeletedWhileLocked_StaysEnforced()
    {
        var enforcer = MakeEnforcer();
        var now = DateTime.Now;
        var data = new PeriodsData { Periods = new List<Period> { MakeHardPeriod("p1", now, -10, 10) } };
        enforcer.ResolveHardLockedPeriods(data, now);

        var phantoms = enforcer.ResolveHardLockedPeriods(new PeriodsData(), now);
        Assert.Single(phantoms);
    }

    [Fact]
    public void ResolveHardLockedPeriods_EndTimeMovedEarlier_IsIgnored()
    {
        var enforcer = MakeEnforcer();
        var now = DateTime.Now;
        var p = MakeHardPeriod("p1", now, -10, 60); // se termine dans 1h
        enforcer.ResolveHardLockedPeriods(new PeriodsData { Periods = new List<Period> { p } }, now);

        // "edition" directe de endTime pour faire croire que la plage vient de se terminer
        var edited = MakeHardPeriod("p1", now, -10, -1);
        var phantoms = enforcer.ResolveHardLockedPeriods(new PeriodsData { Periods = new List<Period> { edited } }, now);
        Assert.Single(phantoms); // l'ancienne echeance (dans ~1h) fait toujours foi
    }

    [Fact]
    public void ResolveHardLockedPeriods_DeletedAfterWindowEnds_NoLongerEnforced()
    {
        var enforcer = MakeEnforcer();
        var now = DateTime.Now;
        var covering = MakeHardPeriod("p1", now, -10, 1); // se termine dans 1 min
        enforcer.ResolveHardLockedPeriods(new PeriodsData { Periods = new List<Period> { covering } }, now);

        var later = now.AddMinutes(2); // après la fin légitime
        Assert.Empty(enforcer.ResolveHardLockedPeriods(new PeriodsData(), later));
    }

    [Fact]
    public void ResolveHardLockedPeriods_NonHardModePeriod_NeverLocked()
    {
        var enforcer = MakeEnforcer();
        var now = DateTime.Now;
        var p = MakeHardPeriod("p1", now, -10, 10);
        p.HardMode = false;
        enforcer.ResolveHardLockedPeriods(new PeriodsData { Periods = new List<Period> { p } }, now);

        Assert.Empty(enforcer.ResolveHardLockedPeriods(new PeriodsData(), now)); // rien a defendre, ce n'etait pas hard mode
    }
}
