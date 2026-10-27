using Microsoft.Data.Sqlite;

namespace SelectLunch.Shared.Tests;

/// <summary>테스트마다 격리된 임시 폴더. 파일 DB가 남긴 핸들을 놓은 뒤 지운다.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "select-lunch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    public string Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더라 남아도 무해하다.
        }
    }
}
