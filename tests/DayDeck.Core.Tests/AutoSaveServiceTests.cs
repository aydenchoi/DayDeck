using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.AutoSave;
using DayDeck.Core.Settings;

namespace DayDeck.Core.Tests;

public sealed class AutoSaveServiceTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly AutoSaveSettings _settings = new() { IntervalMinutes = 5, DebounceSeconds = 3, SaveAfterEdit = true };
    private readonly AutoSaveService _service;

    public AutoSaveServiceTests() => _service = new AutoSaveService(() => _settings, _time, context: null);

    public void Dispose() => _service.Dispose();

    private sealed class Target : IAutoSaveTarget
    {
        public bool IsDirty { get; set; }
        public int SaveCount { get; private set; }
        public bool Throw { get; set; }

        public void Save()
        {
            if (Throw)
            {
                throw new IOException("disk full");
            }
            SaveCount++;
            IsDirty = false;
        }
    }

    [Fact]
    public void Edit_SavesAfterDebounce()
    {
        var target = new Target { IsDirty = true };
        _service.NotifyEdited(target);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, target.SaveCount);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, target.SaveCount);
    }

    [Fact]
    public void ContinuousEditing_PostponesDebounce()
    {
        var target = new Target { IsDirty = true };
        for (var i = 0; i < 5; i++)
        {
            _service.NotifyEdited(target);
            _time.Advance(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(0, target.SaveCount);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, target.SaveCount);
    }

    [Fact]
    public void DebounceOff_SavesOnIntervalOnly()
    {
        _settings.SaveAfterEdit = false;
        var target = new Target { IsDirty = true };
        _service.NotifyEdited(target);

        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, target.SaveCount);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, target.SaveCount);
    }

    [Fact]
    public void ApplySettings_ChangesInterval()
    {
        _settings.SaveAfterEdit = false;
        var target = new Target { IsDirty = true };
        _service.Register(target);

        _settings.IntervalMinutes = 1;
        _service.ApplySettings();
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, target.SaveCount);
    }

    [Fact]
    public void FlushAll_SavesOnlyDirtyTargetsAndRaisesSaved()
    {
        var dirty = new Target { IsDirty = true };
        var clean = new Target();
        _service.Register(dirty);
        _service.Register(clean);
        var savedEvents = 0;
        _service.Saved += (_, _) => savedEvents++;

        Assert.Equal(1, _service.FlushAll());
        Assert.Equal(0, clean.SaveCount);
        Assert.Equal(1, savedEvents);

        Assert.Equal(0, _service.FlushAll());
        Assert.Equal(1, savedEvents);
    }

    [Fact]
    public void FailingTarget_DoesNotBlockOthersAndStaysDirty()
    {
        var failing = new Target { IsDirty = true, Throw = true };
        var ok = new Target { IsDirty = true };
        _service.Register(failing);
        _service.Register(ok);

        Assert.Equal(1, _service.FlushAll());
        Assert.True(failing.IsDirty);
        Assert.True(_service.HasUnsavedChanges);
    }

    [Fact]
    public void Unregister_SavesPendingChanges()
    {
        var target = new Target { IsDirty = true };
        _service.Register(target);

        _service.Unregister(target);

        Assert.Equal(1, target.SaveCount);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, target.SaveCount);
    }
}
