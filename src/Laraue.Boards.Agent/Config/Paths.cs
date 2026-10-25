namespace Laraue.Boards.Agent.Config;

static class Paths
{
    public static string Expand(string path)
    {
        return path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/'))
            : Path.GetFullPath(path);
    }
}
