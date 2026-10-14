using MajdataEdit_NeoX.Base;
using MajdataEdit_NeoX.Models.Setting;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace MajdataEdit_NeoX.Services;

public class SettingsService
{
    private          MajSetting? _currentSettings;
    private readonly Lock        _lock = new();


    public MajSetting          Current => _currentSettings!;
    public event EventHandler? SettingsChanged;

    public SettingsService()
    {
        Load();
    }

    public void Set(Action<MajSetting> setter)
    {
        lock (_lock)
        {
            setter(Current);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void NotifyChanged()
    {
        lock (_lock)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(MajEnv.SettingsFile))
                {
                    var json      = File.ReadAllText(MajEnv.SettingsFile);
                    var root      = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
                    var playerIps = root["EditSetting"]?["PlayerIps"];
                    if (playerIps?.GetValueKind() == JsonValueKind.String)
                        root["EditSetting"]!["PlayerIps"] = new JsonArray(playerIps.GetValue<string>());

                    _currentSettings =   root.Deserialize<MajSetting>();
                    _currentSettings ??= new MajSetting();
                }
                else
                {
                    _currentSettings = new MajSetting();
                    Save(); // Save defaults
                }
            }
            catch (Exception ex)
            {
                // If load fails, use defaults
                Console.WriteLine($"Failed to load settings: {ex.Message}");
                _currentSettings = new MajSetting();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                if (_currentSettings == null)
                {
                    return;
                }

                var directory = Path.GetDirectoryName(MajEnv.SettingsFile);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(MajEnv.SettingsFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save settings: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Reset settings to defaults
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _currentSettings = new MajSetting();
            Save();
        }
    }
}
