using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CsdBridge.Core.Routines
{
    /// <summary>The file changed on disk (someone else saved it) since it was opened.</summary>
    public sealed class RoutineConflictException : Exception
    {
        public RoutineConflictException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The routines in two folders: the user's own (always there) and, optionally, the
    /// office's shared folder on a network drive. One .md file per routine, named after it.
    ///
    /// Names are unique (ignoring case) within a folder; the same name may be in both, e.g. a
    /// personal copy of an office routine. Deleted routines are moved to a "deleted" folder
    /// inside their folder, so they can be recovered.
    /// </summary>
    public sealed class RoutineLibrary
    {
        public const string DeletedFolderName = "deleted";

        private static readonly string[] ReservedFileNames =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public RoutineLibrary(string personalFolder, string? officeFolder)
        {
            PersonalFolder = personalFolder;
            bool sameFolder = !string.IsNullOrWhiteSpace(officeFolder)
                && string.Equals(Full(officeFolder!), Full(personalFolder), StringComparison.OrdinalIgnoreCase);
            OfficeFolder = string.IsNullOrWhiteSpace(officeFolder) || sameFolder ? null : officeFolder!.Trim();
        }

        public string PersonalFolder { get; }

        /// <summary>The shared folder, or null when none is set.</summary>
        public string? OfficeFolder { get; }

        public bool HasOffice => OfficeFolder != null;

        public string FolderFor(RoutineLocation location)
        {
            if (location == RoutineLocation.Office)
            {
                return OfficeFolder ?? throw new InvalidOperationException("No office routines folder is set (office_routines_folder in settings.json).");
            }

            return PersonalFolder;
        }

        /// <summary>Every routine, sorted by name. Folders that can't be read are reported in <paramref name="problems"/>.</summary>
        public List<Routine> LoadAll(out List<string> problems)
        {
            problems = new List<string>();
            var all = new List<Routine>();
            all.AddRange(LoadFolder(RoutineLocation.Personal, problems));
            if (HasOffice)
            {
                if (Directory.Exists(OfficeFolder))
                {
                    all.AddRange(LoadFolder(RoutineLocation.Office, problems));
                }
                else
                {
                    problems.Add($"The office routines folder wasn't found: {OfficeFolder}");
                }
            }

            return all.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Location).ToList();
        }

        /// <summary>The routine with this name (ignoring case): the user's own first, then the office's.</summary>
        public Routine? Find(string name)
        {
            string wanted = (name ?? "").Trim();
            if (wanted.Length == 0)
            {
                return null;
            }

            var ignored = new List<string>();
            foreach (RoutineLocation location in Locations())
            {
                Routine? found = LoadFolder(location, ignored).FirstOrDefault(r => SameName(r.Name, wanted));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>The routine with this name (ignoring case) in one folder, or null.</summary>
        public Routine? Find(string name, RoutineLocation location)
        {
            if (location == RoutineLocation.Office && !HasOffice)
            {
                return null;
            }

            var ignored = new List<string>();
            return LoadFolder(location, ignored).FirstOrDefault(r => SameName(r.Name, name ?? ""));
        }

        /// <summary>True if another file in that folder already has this name.</summary>
        public bool NameTaken(string name, RoutineLocation location, string? exceptPath = null)
        {
            var ignored = new List<string>();
            return LoadFolder(location, ignored).Any(r => SameName(r.Name, name) && !SamePath(r.FilePath, exceptPath));
        }

        /// <summary>
        /// Save a routine to a folder. <paramref name="original"/> is the routine as it was
        /// loaded when this is an edit (it may be renamed or moved to the other folder), or
        /// null for a new one. Throws <see cref="RoutineConflictException"/> if the original
        /// file changed since it was read, unless <paramref name="overwriteChanges"/>.
        /// </summary>
        public Routine Save(Routine routine, RoutineLocation location, Routine? original = null, bool overwriteChanges = false)
        {
            if (Routine.CheckName(routine.Name) is string problem)
            {
                throw new ArgumentException(problem);
            }

            string name = routine.Name.Trim();
            string folder = FolderFor(location);
            string? originalPath = original?.FilePath;
            bool originalExists = originalPath != null && File.Exists(originalPath);

            if (originalExists && !overwriteChanges && original!.LoadedWriteTimeUtc is DateTime loaded
                && File.GetLastWriteTimeUtc(originalPath!) != loaded)
            {
                throw new RoutineConflictException(
                    $"\"{original.Name}\" was changed by someone else after you opened it ({File.GetLastWriteTime(originalPath!).ToString("g", CultureInfo.CurrentCulture)}).");
            }

            if (NameTaken(name, location, originalExists ? originalPath : null))
            {
                throw new ArgumentException($"There's already a routine called \"{name}\" in {Routine.LocationName(location)}.");
            }

            Directory.CreateDirectory(folder);
            string path = PathFor(name, folder, originalExists ? originalPath : null);

            var saved = routine.Clone();
            saved.Name = name;
            File.WriteAllText(path, RoutineFile.Write(saved), new UTF8Encoding(false));

            // Renamed or moved: remove the old file. (A change of case only keeps the same file.)
            if (originalExists && !SamePath(originalPath, path))
            {
                File.Delete(originalPath!);
            }

            saved.FilePath = path;
            saved.Location = location;
            saved.LoadedWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            saved.LoadProblem = null;
            return saved;
        }

        public Routine Rename(Routine routine, string newName, bool overwriteChanges = false)
        {
            Routine renamed = routine.Clone();
            renamed.Name = (newName ?? "").Trim();
            return Save(renamed, routine.Location, routine, overwriteChanges);
        }

        /// <summary>A copy named "Name (copy)" (or "(copy 2)", ...) in the given folder.</summary>
        public Routine Duplicate(Routine routine, RoutineLocation location)
        {
            Routine copy = routine.Clone();
            copy.Name = UniqueName(routine.Name.Trim() + " (copy)", location);
            copy.FilePath = null;
            copy.LoadedWriteTimeUtc = null;
            return Save(copy, location);
        }

        /// <summary>Move the routine's file to the "deleted" folder next to it. Returns where it went.</summary>
        public string Delete(Routine routine)
        {
            if (routine.FilePath == null || !File.Exists(routine.FilePath))
            {
                throw new FileNotFoundException($"\"{routine.Name}\" has already been deleted or moved.", routine.FilePath);
            }

            string folder = Path.Combine(Path.GetDirectoryName(routine.FilePath) ?? ".", DeletedFolderName);
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, Path.GetFileName(routine.FilePath));
            if (File.Exists(target))
            {
                target = Path.Combine(folder,
                    Path.GetFileNameWithoutExtension(routine.FilePath) + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ".md");
            }

            File.Move(routine.FilePath, target);
            return target;
        }

        /// <summary>The name, or the name with " 2", " 3"... if it is taken in that folder.</summary>
        public string UniqueName(string name, RoutineLocation location)
        {
            string trimmed = name.Trim();
            if (trimmed.Length > Routine.MaxNameLength - 4)
            {
                trimmed = trimmed.Substring(0, Routine.MaxNameLength - 4).TrimEnd();
            }

            var ignored = new List<string>();
            var taken = new HashSet<string>(LoadFolder(location, ignored).Select(r => r.Name.Trim()), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(trimmed))
            {
                return trimmed;
            }

            string stem = trimmed.EndsWith(")", StringComparison.Ordinal) ? trimmed.Substring(0, trimmed.Length - 1) : trimmed;
            string close = trimmed.EndsWith(")", StringComparison.Ordinal) ? ")" : "";
            for (int n = 2; ; n++)
            {
                string candidate = $"{stem} {n}{close}";
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        /// <summary>A readable, safe file name for a routine name (without folder or extension).</summary>
        public static string FileStem(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim())
            {
                bool safe = char.IsLetterOrDigit(c) || " -_.(),&+'".IndexOf(c) >= 0;
                sb.Append(safe ? c : '-');
            }

            string s = sb.ToString().Trim(' ', '.', '-');
            while (s.Contains("  "))
            {
                s = s.Replace("  ", " ");
            }

            if (s.Length > Routine.MaxNameLength)
            {
                s = s.Substring(0, Routine.MaxNameLength).TrimEnd(' ', '.');
            }

            if (s.Length == 0)
            {
                s = "routine";
            }

            string bare = s.Split('.')[0].Trim();
            if (ReservedFileNames.Any(r => string.Equals(r, bare, StringComparison.OrdinalIgnoreCase)))
            {
                s = "_" + s;
            }

            return s;
        }

        // ------------------------------------------------------------------ helpers

        private IEnumerable<RoutineLocation> Locations()
        {
            yield return RoutineLocation.Personal;
            if (HasOffice)
            {
                yield return RoutineLocation.Office;
            }
        }

        private List<Routine> LoadFolder(RoutineLocation location, List<string> problems)
        {
            var result = new List<Routine>();
            string folder = FolderFor(location);
            if (!Directory.Exists(folder))
            {
                return result;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.md");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                problems.Add($"Couldn't read {Routine.LocationName(location)} ({folder}): {ex.Message}");
                return result;
            }

            foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Routine routine = RoutineFile.Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
                    routine.FilePath = file;
                    routine.Location = location;
                    routine.LoadedWriteTimeUtc = File.GetLastWriteTimeUtc(file);
                    result.Add(routine);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    problems.Add($"Couldn't read {file}: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>The file for a name in a folder. Keeps the current file when only the case changes; never takes another routine's file.</summary>
        private static string PathFor(string name, string folder, string? currentPath)
        {
            string stem = FileStem(name);
            string candidate = Path.Combine(folder, stem + ".md");
            if (currentPath != null && string.Equals(Full(candidate), Full(currentPath), StringComparison.OrdinalIgnoreCase))
            {
                return currentPath;
            }

            for (int n = 2; File.Exists(candidate); n++)
            {
                candidate = Path.Combine(folder, $"{stem} ({n}).md");
                if (currentPath != null && string.Equals(Full(candidate), Full(currentPath), StringComparison.OrdinalIgnoreCase))
                {
                    return currentPath;
                }
            }

            return candidate;
        }

        private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool SamePath(string? a, string? b) =>
            a != null && b != null && string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

        private static string Full(string path)
        {
            try
            {
                return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                return path.Trim();
            }
        }
    }
}
