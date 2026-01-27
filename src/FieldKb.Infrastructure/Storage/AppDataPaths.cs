using System;
using System.IO;

namespace FieldKb.Infrastructure.Storage;

public static class AppDataPaths
{
    public static string GetBaseDirectory()
    {
        return AppContext.BaseDirectory;
    }

    public static string GetConfigDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "config");
    }

    public static string GetAppSettingsPath()
    {
        return Path.Combine(GetConfigDirectory(), "appsettings.json");
    }

    public static string GetAppDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "FieldKb");
    }

    public static string GetDatabasePath()
    {
        return Path.Combine(GetAppDataDirectory(), "kb.sqlite");
    }

    public static string GetAttachmentsDirectory()
    {
        return Path.Combine(GetAppDataDirectory(), "attachments");
    }

    public static string GetLogsDirectory()
    {
        return Path.Combine(GetAppDataDirectory(), "logs");
    }
}
