using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Serilog;

using OpenUtau.Api;
using OpenUtau.Core;

namespace PhonemizerOnUtau
{
    public static class PhonemizerLoader
    {
        public static PhonemizerFactory[] LoadAllPhonemizers(){
            const string kBuiltin = "OpenUtau.Plugin.Builtin.dll";
            var phonemizerFactories = new List<PhonemizerFactory>();
            var files = new List<string>();
            files.Add(Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory), kBuiltin));
            Directory.CreateDirectory(PathManager.Inst.PluginsPath);
            files.AddRange(Directory.EnumerateFiles(PathManager.Inst.PluginsPath, "*.dll", SearchOption.AllDirectories));

            foreach (var file in files) {
                Assembly assembly;
                try {
                    if (!LibraryLoader.IsManagedAssembly(file)) {
                        Log.Information($"Skipping {file}");
                        continue;
                    }
                    assembly = Assembly.LoadFile(file);
                    foreach (var type in assembly.GetExportedTypes()) {
                        if (!type.IsAbstract && type.IsSubclassOf(typeof(Phonemizer))) {
                            phonemizerFactories.Add(PhonemizerFactory.Get(type));
                        }
                    }
                } catch (Exception e) {
                    Log.Warning(e, $"Failed to load {file}.");
                    continue;
                }
            }
            /*
            foreach (var type in GetType().Assembly.GetExportedTypes()) {
                if (!type.IsAbstract && type.IsSubclassOf(typeof(Phonemizer))) {
                    phonemizerFactories.Add(PhonemizerFactory.Get(type));
                }
            }*/
            return phonemizerFactories.OrderBy(factory => factory.tag).ToArray();
        }
    }
}
