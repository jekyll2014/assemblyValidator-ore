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

        private class AssemblyInfoItem
        {
            public string fullFilename;
            public string filePath;
            public string fileName;
            public Version fileVersion;
            public List<FileItem> referenceList;
            public bool processed;

        }

        private class FileItem
        {
            public readonly string fullFileName;
            public string FilePath => FilePath(this.fullFileName);
            public string FileName => FileName(this.fullFileName);
            public Version fileVersion;

            public FileItem(string srcFullFileName, Version expectedVersion)
            {
                this.fullFileName = srcFullFileName;
                this.fileVersion = expectedVersion;
            }
        }


        private class ReportItem
        {
            public List<FileItem> fromFileList;
            public FileItem assemblyFile;

            public ReportItem(string asmFullFileName, Version asmFileVersion)
            {
                this.assemblyFile = new FileItem(asmFullFileName, asmFileVersion);
                this.fromFileList = new List<FileItem>();
            }
        }

        private static AssemblyInfoItem GetAssemblyInfo(string fullFileName)
        {
            var assemblyItem = new AssemblyInfoItem
            {
                fullFilename = fullFileName,
                filePath = FilePath(fullFileName),
                fileName = FileName(fullFileName),
                fileVersion = new Version(0, 0),
                referenceList = new List<FileItem>()
            };

            AssemblyName[] references = null;
            try
            {
                AssemblyLoadContext ctx = new AssemblyLoadContext(nameof(AssemblyLoader) + "", true);
                var asm = ctx.LoadFromAssemblyPath(fullFileName);
                try
                {
                    assemblyItem.fileVersion = asm.GetName().Version;
                    references = asm.GetReferencedAssemblies();
                }
                catch (Exception e)
                {
                    //Console.WriteLine("Exception: " + e.Message);
                }
                finally
                {
                    ctx.Unload();
                }

                foreach (var dllReference in references)
                {
                    assemblyItem.referenceList.Add(new FileItem(dllReference.Name + ".dll", dllReference.Version));
                }
            }
            catch
            {
                // .NET Core 3.0 do not support ReflectionOnly load
                /*var loadedAssembly = Assembly.ReflectionOnlyLoadFrom(fullFileName);
                assemblyItem.fileVersion = loadedAssembly.GetName().Version;
                references = loadedAssembly.GetReferencedAssemblies();*/
                try
                {
                    var asm = AssemblyName.GetAssemblyName(fullFileName);
                    assemblyItem.fileVersion = asm.Version;
                }
                catch (Exception e)
                {
                    //Console.WriteLine("Exception: " + e.Message);
                }
            }
            return assemblyItem;
        }

        private static readonly string helpString = "Usage: assemblyValidator.exe [/r] rootDir"
            + Environment.NewLine
            + "/r - recursive (optional)"
            + Environment.NewLine
            + "Errorlevel 0 = no problems found"
            + Environment.NewLine
            + "Errorlevel 1 = no root folder specified"
            + Environment.NewLine
            + "Errorlevel 2 = file search error"
            + Environment.NewLine
            + "Errorlevel 3 = recoverable errors found"
            + Environment.NewLine
            + "Errorlevel 4 = unrecoverable errors found";

        private static int Main(string[] args)
        {
            if (!(args.Length == 1 ||
                (args.Length == 2 &&
                (args[0] == "-r" || args[0] == "/r"))))
            {
                Console.WriteLine("No root folder specified.");
                Console.WriteLine(helpString);
                return 1;
            }

            const string configFileType = "*.config";
            string[] dllFileType = { "*.exe", "*.dll" };
            var rootFolder = args.Length == 1 ? args[0] : args[1];
            string[] filesList = null;

            var recursiveFlag = SearchOption.TopDirectoryOnly;

            if ((args[0] == "-r" || args[0] == "/r"))
            {
                recursiveFlag = SearchOption.AllDirectories;
            }

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
                return 2;
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

                        var rptList = outdatedList.FindAll(x => x.assemblyFile.fullFileName == dllFullFileName);
                        if (rptList?.Count > 1)
                        {
                            Console.WriteLine("Duplicate assembly name in collection: " + dllFullFileName);
                        }

                        if (rptList?.Count <= 0)
                        {
                            var rpt = new ReportItem(dllFullFileName, dllVersion);
                            rpt.fromFileList.Add(fromFileItem);
                            outdatedList.Add(rpt);
                        }
                        else if (rptList?.Count == 1)
                        {
                            rptList[0].fromFileList.Add(fromFileItem);
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
                    return 2;
                }
                foreach (var file in filesList)
                {
                    var newAssembly = GetAssemblyInfo(file);
                    if (newAssembly != null)
                    {
                        assemblyList.Add(newAssembly);
                    }
                }
            }

            // check references for files grouped by folder
            var stop = false;
            while (!stop)
            {
                List<AssemblyInfoItem> activeFiles = new List<AssemblyInfoItem>();
                activeFiles = assemblyList?.FindAll(x => !x.processed);
                if (activeFiles == null || activeFiles.Count <= 0)
                {
                    stop = true;
                    continue;
                }

                var currentPath = activeFiles[0].filePath;
                var folderFiles = assemblyList.FindAll(x => x.filePath == currentPath);
                foreach (var srcDllFile in folderFiles)
                {
                    srcDllFile.processed = true;
                    if (srcDllFile.referenceList == null || srcDllFile.referenceList.Count <= 0)
                    {
                        continue;
                    }

                    foreach (var refFile in srcDllFile.referenceList)
                    {
                        var foundFiles = folderFiles.FindAll(x => x.fileName == refFile.FileName);
                        if (foundFiles.Count > 0)
                        {
                            if (foundFiles.Count > 1)
                            {
                                Console.WriteLine("Duplicate assembly name in collection: " + refFile.FileName);
                            }

                            if (foundFiles[0].fileVersion < refFile.fileVersion)
                            {
                                var fromFileItem = new FileItem(srcDllFile.fullFilename, refFile.fileVersion);

                                var rptList = outdatedList.FindAll(x => x.assemblyFile.fullFileName == foundFiles[0].fullFilename);
                                if (rptList.Count <= 0)
                                {
                                    var rpt = new ReportItem(foundFiles[0].fullFilename, foundFiles[0].fileVersion);
                                    rpt.fromFileList.Add(fromFileItem);
                                    outdatedList.Add(rpt);
                                }
                                else
                                {
                                    rptList[0].fromFileList.Add(fromFileItem);
                                }
                            }
                        }
                    }
                }
            }

            var errorLevel = 0;
            // generate report to console
            // generate batch file to get correct files if any
            if (outdatedList.Count > 0)
            {
                errorLevel = 3;
                Console.WriteLine("Assembly files reference check:");
                var currentDir = "";
                var copyCommand = new StringBuilder();
                foreach (var report in outdatedList)
                {
                    if (report.assemblyFile.FilePath != currentDir)
                    {
                        currentDir = report.assemblyFile.FilePath;
                        Console.WriteLine(currentDir + ":");
                    }

                    Console.WriteLine("\t"
                        + report.assemblyFile.FileName
                        + " v."
                        + report.assemblyFile.fileVersion
                        + " outdated");

                    foreach (var refFile in report.fromFileList)
                    {
                        Console.WriteLine("\t\tv."
                                            + refFile.fileVersion
                                            + " expected by "
                                            + refFile.FileName);

                        var correctFile = assemblyList.Find(x => x.fileName == report.assemblyFile.FileName && x.fileVersion == refFile.fileVersion);
                        if (correctFile != null)
                        {
                            copyCommand.AppendLine("rem v." + correctFile.fileVersion + " => " + report.assemblyFile.fileVersion);
                            copyCommand.AppendLine("copy " + correctFile.fullFilename + " " + report.assemblyFile.fullFileName);
                        }
                        else
                        {
                            copyCommand.AppendLine("rem v." + refFile.fileVersion + " => " + report.assemblyFile.fileVersion);
                            copyCommand.AppendLine("rem copy " + "_from_repository_" + " " + report.assemblyFile.fullFileName);
                            errorLevel = 4;
                        }
                    }
                }

                if (copyCommand.Length > 0)
                {
                    File.WriteAllText("fix.bat", copyCommand.ToString());
                }
            }

            return errorLevel;
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
