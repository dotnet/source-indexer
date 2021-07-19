using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Microsoft.SourceBrowser.BinLogParser
{
    public class CompilerInvocation
    {
        public string ProjectFilePath { get; set; }
        public string ProjectDirectory => ProjectFilePath == null ? "" : Path.GetDirectoryName(ProjectFilePath);
        public string OutputAssemblyPath { get; set; }
        public string CommandLineArguments { get; set; }
        public string SolutionRoot { get; set; }
        public IEnumerable<string> TypeScriptFiles { get; set; }

        public string AssemblyName => Path.GetFileNameWithoutExtension(OutputAssemblyPath);

        private string language;
        public string Language
        {
            get
            {
                if (language == null)
                {
                    if (ProjectFilePath == null && TypeScriptFiles != null)
                    {
                        language = "TypeScript";
                    }
                    else if (".vbproj".Equals(Path.GetExtension(ProjectFilePath), StringComparison.OrdinalIgnoreCase))
                    {
                        language = "Visual Basic";
                    }
                    else
                    {
                        language = "C#";
                    }
                }

                return language;
            }

            set
            {
                language = value;
            }
        }

        private string[] GetCommandLineArguments()
        {
            return CommandLineParser.SplitCommandLineIntoArguments(CommandLineArguments, removeHashComments: false).ToArray();
        }

        private CommandLineArguments parsed;
        public CommandLineArguments Parsed
        {
            get
            {
                if (parsed != null)
                {
                    return parsed;
                }

                var sdkDirectory = RuntimeEnvironment.GetRuntimeDirectory();
                var args = GetCommandLineArguments();

                if (Language == LanguageNames.CSharp)
                {
                    parsed = CSharpCommandLineParser.Default.Parse(args, ProjectDirectory, sdkDirectory);
                }
                else
                {
                    parsed = VisualBasicCommandLineParser.Default.Parse(args, ProjectDirectory, sdkDirectory);
                }

                return parsed;
            }
        }

        public override string ToString()
        {
            return CommandLineArguments;
        }
    }
}
