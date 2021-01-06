using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.SourceBrowser.Common;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class TypeForwardReader : MarshalByRefObject
    {
        public IEnumerable<Tuple<string, string, string>> GetTypeForwards(string path, IReadOnlyDictionary<string, string> properties)
        {
            IEnumerable<Tuple<string, string, string>> result;
            try
            {
                result = GetTypeForwardsImpl(path, properties).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                result = Array.Empty<Tuple<string, string, string>>();
            }

            Log.Close();
            Log.WaitForCompletion().Wait();
            return result;
        }

        private IEnumerable<Tuple<string, string, string>> GetTypeForwardsImpl(string path, IReadOnlyDictionary<string, string> properties)
        {
            var workspace = SolutionGenerator.CreateWorkspace(properties.ToImmutableDictionary());
            Solution solution;
            if (path.EndsWith(".sln"))
            {
                solution = workspace.OpenSolutionAsync(path).GetAwaiter().GetResult();
            }
            else
            {
                solution = workspace.OpenProjectAsync(path).GetAwaiter().GetResult().Solution;
            }

            var assemblies = solution.Projects.Select(p => p.OutputFilePath).ToList();
            foreach (var assemblyFile in assemblies)
            {
                if (File.Exists(assemblyFile))
                {
                    Log.Write("File exists: " + assemblyFile);
                }
                else
                {
                    Log.Write("File doesn't exist: " + assemblyFile);
                    continue;
                }

                foreach (Tuple<string, string, string> tuple in ReadTypeForwardsFromAssembly(assemblyFile))
                    yield return tuple;
            }
        }

        public static IEnumerable<Tuple<string, string, string>> ReadTypeForwardsFromAssembly(string assemblyFile)
        {
            var thisAssemblyName = Path.GetFileNameWithoutExtension(assemblyFile);
            using (var peReader = new PEReader(File.ReadAllBytes(assemblyFile).ToImmutableArray()))
            {
                var reader = peReader.GetMetadataReader();
                foreach (var exportedTypeHandle in reader.ExportedTypes)
                {
                    var exportedType = reader.GetExportedType(exportedTypeHandle);
                    var result = ProcessExportedType(exportedType, reader, thisAssemblyName);
                    if (result != null)
                    {
                        Log.Write(result.ToString());
                        yield return result;
                    }
                }
            }
        }

        private static string GetFullName(MetadataReader reader, ExportedType type)
        {
            Debug.Assert(type.IsForwarder);
            if (type.Implementation.Kind == HandleKind.AssemblyReference)
            {
                var name = reader.GetString(type.Name);
                var ns = type.Namespace.IsNil ? null : reader.GetString(type.Namespace);
                var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                return fullName;
            }
            if (type.Implementation.Kind == HandleKind.ExportedType)
            {
                var name = reader.GetString(type.Name);
                Debug.Assert(type.Namespace.IsNil);
                return GetFullName(reader, reader.GetExportedType((ExportedTypeHandle)type.Implementation)) + "." + name;
            }
            throw new NotSupportedException(type.Implementation.Kind.ToString());
        }

        private static string GetAssemblyName(MetadataReader reader, ExportedType type)
        {
            Debug.Assert(type.IsForwarder);
            if (type.Implementation.Kind == HandleKind.AssemblyReference)
            {
                return reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.Implementation).Name);
            }
            if (type.Implementation.Kind == HandleKind.ExportedType)
            {
                return GetAssemblyName(reader, reader.GetExportedType((ExportedTypeHandle)type.Implementation));
            }
            throw new NotSupportedException(type.Implementation.Kind.ToString());
        }

        private static Tuple<string, string, string> ProcessExportedType(ExportedType exportedType, MetadataReader reader, string thisAssemblyName)
        {
            if (!exportedType.IsForwarder) return null;
            return Tuple.Create(thisAssemblyName, "T:" + GetFullName(reader, exportedType), GetAssemblyName(reader, exportedType));
        }
    }
}
