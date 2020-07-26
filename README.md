# assemblyValidatorCore
Assembly reference validation for selected folder

Command line tool to check program folder for absent/incorrect references to .dll and .exe files.
It compares each .dll and .exe file found for references to other .dll and .exe if they are found in the same folder and vompares file version against the reference version.

.NET CORE version works 2-3 times faster than .NET 4.8.

To do: cross reference check for different vesions of the same file name.
