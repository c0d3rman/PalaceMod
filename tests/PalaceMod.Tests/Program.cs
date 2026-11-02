using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PalaceMod.Tests
{
    /// <summary>Minimal test runner: every public static void method of a class named *Tests is a test.</summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            int passed = 0, failed = 0;
            Type[] types;
            try
            {
                types = typeof(Program).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types; // contract types that reference UnityEngine can't load here, and aren't tests
            }
            foreach (var type in types)
            {
                if (type == null || type.Namespace != typeof(Program).Namespace || !type.Name.EndsWith("Tests")) continue;
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.ReturnType != typeof(void) || m.GetParameters().Length != 0) continue;
                    string name = $"{type.Name}.{m.Name}";
                    if (args.Length > 0 && !name.Contains(args[0])) continue;
                    try
                    {
                        m.Invoke(null, null);
                        passed++;
                        Console.WriteLine($"  ok    {name}");
                    }
                    catch (TargetInvocationException e)
                    {
                        failed++;
                        Console.WriteLine($"  FAIL  {name}: {e.InnerException?.Message}");
                        Console.WriteLine(e.InnerException?.StackTrace);
                    }
                }
            }
            Console.WriteLine($"{passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
    }

    public sealed class AssertException(string message) : Exception(message);

    public static class Assert
    {
        public static void True(bool condition, string what)
        {
            if (!condition) throw new AssertException(what);
        }

        public static void Equal<T>(T expected, T actual, string what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertException($"{what ?? "values differ"}: expected <{expected}>, got <{actual}>");
        }

        public static void Null(object value, string what)
        {
            if (value != null) throw new AssertException($"{what}: expected null, got {value}");
        }

        public static void NotNull(object value, string what)
        {
            if (value == null) throw new AssertException($"{what}: was null");
        }
    }

    /// <summary>A temporary folder deleted on dispose.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "palacemod-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { }
        }
    }
}
