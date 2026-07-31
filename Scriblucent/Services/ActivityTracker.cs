using Scriblucent.Models;
using Scriblucent.Services;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace Scriblucent.Services;

public static class ActivityTracker
{
    private static DispatcherTimer? _timer;
    private static DateTime _startTime;

    public static void Initialize()
    {
        _startTime = DateTime.Now;

        var settings = AppSettingsService.Load();
        settings.LastActiveDate = DateTime.Now;
        AppSettingsService.Save(settings);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (s, e) =>
        {
            var currentSettings = AppSettingsService.Load();
            currentSettings.TotalTimeSpentSeconds += 60;
            currentSettings.LastActiveDate = DateTime.Now;
            AppSettingsService.Save(currentSettings);
            ActivityUpdated?.Invoke();
        };
        _timer.Start();

        // Save immediately remaining time on exit is hard, but updating every minute is good enough.
        Application.Current.Exit += (s, e) =>
        {
            var elapsed = (DateTime.Now - _startTime).TotalSeconds % 60;
            var finalSettings = AppSettingsService.Load();
            finalSettings.TotalTimeSpentSeconds += (long)elapsed;
            finalSettings.LastActiveDate = DateTime.Now;
            AppSettingsService.Save(finalSettings);
        };
    }

    public static event Action? ActivityUpdated;

    public static void RecordCharacterTyped()
    {
        var settings = AppSettingsService.Load();
        settings.TotalCharactersTyped++;
        AppSettingsService.Save(settings);
        ActivityUpdated?.Invoke();
    }

    public static void RecordWordTyped()
    {
        var settings = AppSettingsService.Load();
        settings.TotalWordsTyped++;
        AppSettingsService.Save(settings);
    }
    
    public static void RecordTyping(int charactersDelta, int wordsDelta)
    {
        if (charactersDelta == 0 && wordsDelta == 0) return;
        var settings = AppSettingsService.Load();
        if (charactersDelta > 0) settings.TotalCharactersTyped += charactersDelta;
        if (wordsDelta > 0) settings.TotalWordsTyped += wordsDelta;
        AppSettingsService.Save(settings);
        ActivityUpdated?.Invoke();
    }

    public static void Reset()
    {
        _startTime = DateTime.Now;

        var settings = AppSettingsService.Load();
        settings.TotalTimeSpentSeconds = 0;
        settings.TotalWordsTyped = 0;
        settings.TotalCharactersTyped = 0;
        settings.LastActiveDate = DateTime.Now;
        AppSettingsService.Save(settings);
        ActivityUpdated?.Invoke();
    }
}
