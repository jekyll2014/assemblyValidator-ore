using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Xml;

namespace assemblyValidatorCore
{
    class Program
    {
        class AssemblyLoader : MarshalByRefObject
        {
            Assembly assembly;
            public void Load(string file)
            {
                this.assembly = Assembly.ReflectionOnlyLoadFrom(file);
            }

            public Version GetVersion()
            {
                return this.assembly.GetName().Version;
            }

            public AssemblyName[] GetReferences()
            {
                return this.assembly.GetReferencedAssemblies();
            }
        }

        private class FileItem
        {
            public string FullFileName;
            public string FilePath => FilePath(this.FullFileName);
            public string FileName => FileName(this.FullFileName);
            public Version FileVersion;

            public FileItem()
            {
            }

            public FileItem(string fullFileName, Version fileVersion)
            {
                this.FullFileName = fullFileName;
                this.FileVersion = fileVersion;
            }
        }

        private class AssemblyInfoItem : FileItem
        {
            public List<FileItem> referenceList;
            public bool processed;
        }

        private class ReportItem
        {
            public List<FileItem> CalledFromFiles;
            public FileItem AssemblyFile;

            public ReportItem(string asmFullFileName, Version asmFileVersion)
            {
                this.AssemblyFile = new FileItem(asmFullFileName, asmFileVersion);
                this.CalledFromFiles = new List<FileItem>();
            }
        }

        private class CrossReportItem
        {
            public string AssemblyFile;
            public Version AssemblyFileVersion;
            public Dictionary<Version, string> CalledFromFiles;

            public CrossReportItem(string asmFullFileName, Version asmVersion)
            {
                this.AssemblyFile = asmFullFileName;
                this.AssemblyFileVersion = asmVersion;
                this.CalledFromFiles = new Dictionary<Version, string>();
            }
        }

        private static AssemblyInfoItem GetAssemblyInfo(string fullFileName)
        {
            var assemblyItem = new AssemblyInfoItem
            {
                FullFileName = fullFileName,
                FileVersion = new Version(0, 0),
                referenceList = new List<FileItem>()
            };

            AssemblyName[] references = null;
            try
            {
                var ctx = new AssemblyLoadContext(nameof(AssemblyLoader) + "", true);
                var asm = ctx.LoadFromAssemblyPath(fullFileName);
                try
                {
                    assemblyItem.FileVersion = asm.GetName().Version;
                    references = asm.GetReferencedAssemblies();
                }
                catch (Exception e)
                {
                    if (verbose)
                    {
                        Console.WriteLine("Can't get assembly info: " + fullFileName);
                        Console.WriteLine("Exception: " + e.Message);
                    }

                    try
                    {
                        if (verbose)
                        {
                            Console.WriteLine("Re-trying to load assembly: " + fullFileName);
                        }

                        var newAsm = AssemblyName.GetAssemblyName(fullFileName);
                        assemblyItem.FileVersion = newAsm.Version;
                    }
                    catch (Exception e2)
                    {
                        if (verbose)
                        {
                            Console.WriteLine("Still can't load assembly: " + fullFileName);
                            Console.WriteLine("Exception: " + e2.Message);
                        }
                    }
                }
                finally
                {
                    ctx.Unload();
                }

                if (references != null)
                {
                    foreach (var dllReference in references)
                    {
                        assemblyItem.referenceList.Add(new FileItem(dllReference.Name + ".dll", dllReference.Version));
                    }
                }
            }
            catch (Exception e)
            {
                // .NET Core 3.0 do not support ReflectionOnly load
                /*var loadedAssembly = Assembly.ReflectionOnlyLoadFrom(fullFileName);
                assemblyItem.fileVersion = loadedAssembly.GetName().Version;
                references = loadedAssembly.GetReferencedAssemblies();*/
                if (verbose)
                {
                    Console.WriteLine("Can't load assembly: " + fullFileName);
                    Console.WriteLine("Exception: " + e.Message);
                }

                try
                {
                    if (verbose)
                    {
                        Console.WriteLine("Re-trying to load assembly: " + fullFileName);
                    }

                    var newAsm = AssemblyName.GetAssemblyName(fullFileName);
                    assemblyItem.FileVersion = newAsm.Version;
                }
                catch (Exception e2)
                {
                    if (verbose)
                    {
                        Console.WriteLine("Still can't load assembly: " + fullFileName);
                        Console.WriteLine("Exception: " + e2.Message);
                    }
                }
            }

            return assemblyItem;
        }

        private static readonly string helpString = "Usage: assemblyValidatorCore.exe [/r] [/c] [/v] rootDir"
            + Environment.NewLine
            + "/r - recursive (optional)"
            + "/c - check cross-references (optional)"
            + "/v - verbose logging (optional)"
            + Environment.NewLine
            + "Errorlevel 0 = no problems found"
            + Environment.NewLine
            + "Errorlevel 1 = no root folder specified"
            + Environment.NewLine
            + "Errorlevel 2 = file search error"
            + Environment.NewLine
            + "Errorlevel 3 = multiple version references found"
            + Environment.NewLine
            + "Errorlevel 4 = recoverable errors found"
            + Environment.NewLine
            + "Errorlevel 5 = unrecoverable errors found";

        enum ErrorLevel
        {
            NoError = 0,
            MissingRootFolder = 1,
            FileSearchError = 2,
            MultiReference = 3,
            RecoverableErrors = 4,
            UnRecoverableErrors = 5
        }

        static bool verbose = false;

        private static int Main(string[] args)
        {
            var recursiveFlag = SearchOption.TopDirectoryOnly;
            var enableCrossCheck = false;
            var rootFolder = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-r" || args[i] == "/r")
                    recursiveFlag = SearchOption.AllDirectories;
                else if (args[i] == "-c" || args[i] == "/c")
                    enableCrossCheck = true;
                else if (args[i] == "-v" || args[i] == "/v")
                    verbose = true;
                else if (string.IsNullOrEmpty(rootFolder))
                    rootFolder = args[i];
            }

            if (string.IsNullOrEmpty(rootFolder))
            {
                Console.WriteLine("No root folder specified.");
                Console.WriteLine(helpString);
                return (int)ErrorLevel.MissingRootFolder;
            }

            const string configFileType = "*.config";
            string[] dllFileType = { "*.exe", "*.dll" };
            string[] filesList = null;

            var outdatedList = new List<ReportItem>();

            //check .config file references
            try
            {
                filesList = Directory.GetFiles(rootFolder, configFileType, recursiveFlag);
            }
            catch (Exception e)
            {
                Console.WriteLine("File search exception: "
                    + e
                    + Environment.NewLine
                    + "Possibly a file system link found.");
                return (int)ErrorLevel.FileSearchError;
            }

            foreach (var fromFile in filesList)
            {
                var configReport = new StringBuilder();
                var config = new XmlDocument();

                try
                {
                    config.Load(fromFile);
                }
                catch
                {
                    // not XML file, skip it
                    continue;
                }

                var assemblyNodes = config.GetElementsByTagName("dependentAssembly");
                if (assemblyNodes.Count <= 0)
                {
                    // no assembly references, skip it
                    continue;
                }

                // process each assembly reference in the .config file
                foreach (XmlNode node in assemblyNodes)
                {
                    // get assembly name from config
                    var dllFileNode = node["assemblyIdentity"];
                    if (dllFileNode == null)
                    {
                        // no DLL name fixed in XML, skip it
                        continue;
                    }

                    var dllFileName = "";
                    foreach (XmlAttribute attribute in dllFileNode.Attributes)
                    {
                        if (attribute.Name == "name")
                        {
                            // DLL name tag found in XML
                            dllFileName = attribute.Value;
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(dllFileName))
                    {
                        // DLL name tag not found in XML, skip it
                        continue;
                    }

                    // get assembly version from config
                    var dllVersionNode = node["bindingRedirect"];
                    if (dllVersionNode == null)
                    {
                        // no DLL version tag found in XML
                        continue;
                    }

                    Version expectedVersion = null;
                    foreach (XmlAttribute attribute in dllVersionNode.Attributes)
                    {
                        if (attribute.Name == "newVersion")
                        {
                            try
                            {
                                expectedVersion = new Version(attribute.Value);
                            }
                            catch
                            {
                            }
                            // DLL version tag found in XML
                            break;
                        }
                    }

                    if (expectedVersion == null)
                    {
                        // DLL version tag not found in XML, skip it
                        continue;
                    }

                    // Get file version.
                    var dllFullFileName = FilePath(fromFile) + dllFileName + ".dll";
                    Version dllVersion = null;
                    try
                    {
                        dllVersion = AssemblyName.GetAssemblyName(dllFullFileName).Version;
                    }
                    catch
                    {
                        // no such DLL in the folder or error opening it
                        //Console.WriteLine("Can't open file: " + dllPath);
                        continue;
                    }

                    if (dllVersion == null)
                    {
                        // can't get file version, skip it
                        continue;
                    }

                    if (dllVersion < expectedVersion)
                    {
                        var fromFileItem = new FileItem(fromFile, expectedVersion);

                        var rptList = outdatedList.FindAll(x => x.AssemblyFile.FullFileName == dllFullFileName);
                        if (rptList.Count > 1)
                        {
                            Console.WriteLine("Duplicate assembly name in collection: " + dllFullFileName);
                        }

                        if (rptList.Count <= 0)
                        {
                            var rpt = new ReportItem(dllFullFileName, dllVersion);
                            rpt.CalledFromFiles.Add(fromFileItem);
                            outdatedList.Add(rpt);
                        }
                        else if (rptList.Count == 1)
                        {
                            rptList[0].CalledFromFiles.Add(fromFileItem);
                        }

                    }
                }
            }

            // collect folder assembly collection
            var assemblyList = new List<AssemblyInfoItem>();
            foreach (var fileType in dllFileType)
            {
                try
                {
                    filesList = Directory.GetFiles(rootFolder, fileType, recursiveFlag);
                }
                catch (Exception e)
                {
                    Console.WriteLine("File search exception: "
                        + e
                        + Environment.NewLine
                        + "Possibly a file system link found.");
                    return (int)ErrorLevel.FileSearchError;
                }
                foreach (var file in filesList)
                {
                    var newAssembly = GetAssemblyInfo(file);
                    assemblyList.Add(newAssembly);
                }
            }

            var crossList = new List<CrossReportItem>();

            // check references for files grouped by folder
            while (true)
            {
                var activeFiles = new List<AssemblyInfoItem>();
                activeFiles = assemblyList.FindAll(x => !x.processed);
                if (activeFiles == null || activeFiles.Count <= 0)
                {
                    break;
                }

                var currentPath = activeFiles[0].FilePath;
                var folderFiles = assemblyList.FindAll(x => x.FilePath == currentPath);
                foreach (var srcDllFile in folderFiles)
                {
                    srcDllFile.processed = true;

                    //check cross-references for different versions
                    if (enableCrossCheck)
                    {
                        var verList = new CrossReportItem(srcDllFile.FullFileName, srcDllFile.FileVersion);
                        foreach (var refFromFile in folderFiles)
                        {
                            if (srcDllFile.FileName == refFromFile.FileName)
                                continue;

                            foreach (var referenceItem in refFromFile.referenceList)
                            {
                                if (referenceItem.FullFileName == srcDllFile.FileName)
                                {
                                    if (!verList.CalledFromFiles.ContainsKey(referenceItem.FileVersion))
                                    {
                                        verList.CalledFromFiles.Add(referenceItem.FileVersion, refFromFile.FileName);
                                    }
                                }
                            }
                        }

                        if (verList.CalledFromFiles.Count > 1)
                            crossList.Add(verList);
                    }

                    if (srcDllFile.referenceList == null || srcDllFile.referenceList.Count <= 0)
                    {
                        continue;
                    }

                    // check for files with version other than required by caller
                    foreach (var refFile in srcDllFile.referenceList)
                    {
                        var foundFiles = folderFiles.FindAll(x => x.FileName == refFile.FileName);
                        if (foundFiles.Count > 0)
                        {
                            if (foundFiles.Count > 1)
                            {
                                Console.WriteLine("Duplicate assembly name in collection: " + refFile.FileName);
                            }

                            if (foundFiles[0].FileVersion < refFile.FileVersion)
                            {
                                var fromFileItem = new FileItem(srcDllFile.FullFileName, refFile.FileVersion);

                                var rptList = outdatedList.FindAll(x => x.AssemblyFile.FullFileName == foundFiles[0].FullFileName);
                                if (rptList.Count <= 0)
                                {
                                    var rpt = new ReportItem(foundFiles[0].FullFileName, foundFiles[0].FileVersion);
                                    rpt.CalledFromFiles.Add(fromFileItem);
                                    outdatedList.Add(rpt);
                                }
                                else
                                {
                                    rptList[0].CalledFromFiles.Add(fromFileItem);
                                }
                            }
                        }
                    }
                }
            }

            var errorLevel = ErrorLevel.NoError;
            // generate report to console
            // generate batch file to get correct files if any
            if (crossList.Count > 1)
            {
                Console.WriteLine("Assembly files reference check:");
                errorLevel = ErrorLevel.MultiReference;
                foreach (var reportItem in crossList)
                {
                    Console.WriteLine(reportItem.AssemblyFile + "[" + reportItem.AssemblyFileVersion + "] cross-referenced by:");
                    foreach (var fileItem in reportItem.CalledFromFiles)
                    {
                        Console.WriteLine("\tv."
                                          + fileItem.Key
                                          + " expected by "
                                          + fileItem.Value);
                    }
                }
            }

            if (outdatedList.Count > 0)
            {
                Console.WriteLine("Assembly files reference check:");
                errorLevel = ErrorLevel.RecoverableErrors;
                var currentDir = "";
                var copyCommand = new StringBuilder();
                foreach (var report in outdatedList)
                {
                    if (report.AssemblyFile.FilePath != currentDir)
                    {
                        currentDir = report.AssemblyFile.FilePath;
                        Console.WriteLine(currentDir + ":");
                    }

                    Console.WriteLine("\t"
                        + report.AssemblyFile.FileName
                        + " v."
                        + report.AssemblyFile.FileVersion
                        + " outdated");

                    foreach (var refFile in report.CalledFromFiles)
                    {
                        Console.WriteLine("\t\tv."
                                            + refFile.FileVersion
                                            + " expected by "
                                            + refFile.FileName);

                        var correctFile = assemblyList.Find(x => x.FileName == report.AssemblyFile.FileName && x.FileVersion == refFile.FileVersion);
                        if (correctFile != null)
                        {
                            copyCommand.AppendLine("rem v." + correctFile.FileVersion + " => " + report.AssemblyFile.FileVersion);
                            copyCommand.AppendLine("copy " + correctFile.FullFileName + " " + report.AssemblyFile.FullFileName);
                        }
                        else
                        {
                            copyCommand.AppendLine("rem v." + refFile.FileVersion + " => " + report.AssemblyFile.FileVersion);
                            copyCommand.AppendLine("rem copy " + "_from_repository_" + " " + report.AssemblyFile.FullFileName);
                            errorLevel = ErrorLevel.UnRecoverableErrors;
                        }
                    }
                }

                if (copyCommand.Length > 0)
                {
                    File.WriteAllText("fix.bat", copyCommand.ToString());
                }
            }

            return (int)errorLevel;
        }

        private static string FilePath(string fullFileName)
        {
            var i = fullFileName.LastIndexOf('\\');
            return i <= 0 ? "" : fullFileName.Substring(0, i + 1);
        }

        private static string FileName(string fullFileName)
        {
            var i = fullFileName.LastIndexOf('\\');
            return i < 0 ? fullFileName : fullFileName.Substring(i + 1);
        }
    }
}
