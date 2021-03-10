﻿namespace Nerdbank.GitVersioning.Tasks
{
    using System;
    using System.CodeDom;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using Microsoft.Build.Framework;
    using Microsoft.Build.Utilities;

    public class AssemblyVersionInfo : Task
    {
        /// <summary>
        /// The #if expression that surrounds a <see cref="GeneratedCodeAttribute"/> to avoid a compilation failure when targeting the nano framework.
        /// </summary>
        /// <remarks>
        /// See https://github.com/dotnet/Nerdbank.GitVersioning/issues/346
        /// </remarks>
        private const string CompilerDefinesAroundGeneratedCodeAttribute = "NETSTANDARD || NETFRAMEWORK || NETCOREAPP";
        private const string CompilerDefinesAroundExcludeFromCodeCoverageAttribute = "NETFRAMEWORK || NETCOREAPP || NETSTANDARD2_0 || NETSTANDARD2_1";

        public static readonly string GeneratorName = ThisAssembly.AssemblyName;
        public static readonly string GeneratorVersion = ThisAssembly.AssemblyVersion;
#if NET461
        private static readonly CodeGeneratorOptions codeGeneratorOptions = new CodeGeneratorOptions
        {
            BlankLinesBetweenMembers = false,
            IndentString = "    ",
        };

        private CodeCompileUnit generatedFile;
#endif
        private const string FileHeaderComment = @"------------------------------------------------------------------------------
 <auto-generated>
     This code was generated by a tool.
     Runtime Version:4.0.30319.42000

     Changes to this file may cause incorrect behavior and will be lost if
     the code is regenerated.
 </auto-generated>
------------------------------------------------------------------------------
";

        private CodeGenerator generator;

        [Required]
        public string CodeLanguage { get; set; }

        [Required]
        public string OutputFile { get; set; }

        public bool EmitNonVersionCustomAttributes { get; set; }

        public string AssemblyName { get; set; }

        public string AssemblyVersion { get; set; }

        public string AssemblyFileVersion { get; set; }

        public string AssemblyInformationalVersion { get; set; }

        public string RootNamespace { get; set; }

        public string AssemblyOriginatorKeyFile { get; set; }

        public string AssemblyKeyContainerName { get; set; }

        public string AssemblyTitle { get; set; }

        public string AssemblyProduct { get; set; }

        public string AssemblyCopyright { get; set; }

        public string AssemblyCompany { get; set; }

        public string AssemblyConfiguration { get; set; }

        public bool PublicRelease { get; set; }

        public string PrereleaseVersion { get; set; }

        public string GitCommitId { get; set; }

        public string GitCommitDateTicks { get; set; }

        public bool EmitThisAssemblyClass { get; set; } = true;

        /// <summary>
        /// Specify additional fields to be added to the <c>ThisAssembly</c> class.
        /// </summary>
        /// <remarks>
        /// Field name is given by %(Identity). Provide the field value by specifying exactly one metadata value that is %(String), %(Boolean) or %(Ticks) (for UTC DateTime).
        /// If specifying %(String), you can also specify %(EmitIfEmpty) to determine if a class member is added even if the value is an empty string (default is false).
        /// </remarks>
        /// <example>
        /// <![CDATA[
        ///  <ItemGroup>
        ///    <AdditionalThisAssemblyFields Include = "CustomString1" String="Hello, World!"/>
        ///    <AdditionalThisAssemblyFields Include = "CustomString2" String="$(SomeProperty)" EmitIfEmpty="true"/>
        ///    <AdditionalThisAssemblyFields Include = "CustomBool1" Boolean="true"/>
        ///    <AdditionalThisAssemblyFields Include = "CustomDateTime1" Ticks="637505461230000000"/>
        ///  </ItemGroup>
        /// ]]>
        /// </example>
        public ITaskItem[] AdditionalThisAssemblyFields { get; set; }

#if NET461
        public override bool Execute()
        {
            // attempt to use local codegen
            string fileContent = this.BuildCode();
            if (fileContent != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.OutputFile));
                Utilities.FileOperationWithRetry(() => File.WriteAllText(this.OutputFile, fileContent));
            }
            else if (CodeDomProvider.IsDefinedLanguage(this.CodeLanguage))
            {
                using (var codeDomProvider = CodeDomProvider.CreateProvider(this.CodeLanguage))
                {
                    this.generatedFile = new CodeCompileUnit();
                    this.generatedFile.AssemblyCustomAttributes.AddRange(this.CreateAssemblyAttributes().ToArray());

                    var ns = new CodeNamespace();
                    this.generatedFile.Namespaces.Add(ns);

                    if (this.EmitThisAssemblyClass)
                    {
                        ns.Types.Add(this.CreateThisAssemblyClass());
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(this.OutputFile));
                    FileStream file = null;
                    Utilities.FileOperationWithRetry(() => file = File.OpenWrite(this.OutputFile));
                    using (file)
                    {
                        using (var fileWriter = new StreamWriter(file, new UTF8Encoding(true), 4096, leaveOpen: true))
                        {
                            codeDomProvider.GenerateCodeFromCompileUnit(this.generatedFile, fileWriter, codeGeneratorOptions);
                        }

                        // truncate to new size.
                        file.SetLength(file.Position);
                    }
                }
            }
            else
            {
                this.Log.LogError("CodeDomProvider not available for language: {0}. No version info will be embedded into assembly.", this.CodeLanguage);
            }

            return !this.Log.HasLoggedErrors;
        }

        private CodeTypeDeclaration CreateThisAssemblyClass()
        {
            var thisAssembly = new CodeTypeDeclaration("ThisAssembly")
            {
                IsClass = true,
                IsPartial = true,
                TypeAttributes = TypeAttributes.NotPublic | TypeAttributes.Sealed,
            };

            var codeAttributeDeclarationCollection = new CodeAttributeDeclarationCollection();
            codeAttributeDeclarationCollection.Add(new CodeAttributeDeclaration("System.CodeDom.Compiler.GeneratedCode",
                new CodeAttributeArgument(new CodePrimitiveExpression(GeneratorName)),
                new CodeAttributeArgument(new CodePrimitiveExpression(GeneratorVersion))));
            thisAssembly.CustomAttributes = codeAttributeDeclarationCollection;

            // CodeDOM doesn't support static classes, so hide the constructor instead.
            thisAssembly.Members.Add(new CodeConstructor { Attributes = MemberAttributes.Private });

            var fields = this.GetFieldsForThisAssembly();

            foreach (var pair in fields)
            {
                switch (pair.Value.Value)
                {
                    case string stringValue:
                        if (pair.Value.EmitIfEmpty || !string.IsNullOrEmpty(stringValue))
                        {
                            thisAssembly.Members.Add(CreateField(pair.Key, stringValue));
                        }
                        break;

                    case bool boolValue:
                        thisAssembly.Members.Add(CreateField(pair.Key, boolValue));
                        break;

                    case long ticksValue:
                        thisAssembly.Members.AddRange(CreateField(pair.Key, ticksValue).ToArray());
                        break;

                    default:
                        throw new NotImplementedException();
                }
            }

            return thisAssembly;
        }

        private IEnumerable<CodeAttributeDeclaration> CreateAssemblyAttributes()
        {
            yield return DeclareAttribute(typeof(AssemblyVersionAttribute), this.AssemblyVersion);
            yield return DeclareAttribute(typeof(AssemblyFileVersionAttribute), this.AssemblyFileVersion);
            yield return DeclareAttribute(typeof(AssemblyInformationalVersionAttribute), this.AssemblyInformationalVersion);
            if (this.EmitNonVersionCustomAttributes)
            {
                if (!string.IsNullOrEmpty(this.AssemblyTitle))
                {
                    yield return DeclareAttribute(typeof(AssemblyTitleAttribute), this.AssemblyTitle);
                }

                if (!string.IsNullOrEmpty(this.AssemblyProduct))
                {
                    yield return DeclareAttribute(typeof(AssemblyProductAttribute), this.AssemblyProduct);
                }

                if (!string.IsNullOrEmpty(this.AssemblyCompany))
                {
                    yield return DeclareAttribute(typeof(AssemblyCompanyAttribute), this.AssemblyCompany);
                }

                if (!string.IsNullOrEmpty(this.AssemblyCopyright))
                {
                    yield return DeclareAttribute(typeof(AssemblyCopyrightAttribute), this.AssemblyCopyright);
                }
            }
        }

        private static CodeMemberField CreateField<T>(string name, T value)
        {
            return new CodeMemberField(typeof(T), name)
            {
                Attributes = MemberAttributes.Const | MemberAttributes.Assembly,
                InitExpression = new CodePrimitiveExpression(value),
            };
        }

        private static IEnumerable<CodeTypeMember> CreateField(string name, long ticks)
        {
            if ( string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            // internal static System.DateTime GitCommitDate {{ get; }} = new System.DateTime({ticks}, System.DateTimeKind.Utc);");
            
            // For backing field name, try to use name with first char converted to lower case, or otherwise suffix with underscore.
            string fieldName = null;
            var char0 = name[0];
            
            if ( char.IsUpper( char0) )
            {
                fieldName =
                    name.Length == 1
                        ? new string(char.ToLowerInvariant(char0), 1)
                        : new string(char.ToLowerInvariant(char0), 1) + name.Substring(1);
            }
            else
            {
                fieldName = name + "_";
            }

            yield return new CodeMemberField(typeof(DateTime), fieldName)
            {
                Attributes = MemberAttributes.Private,
                InitExpression = new CodeObjectCreateExpression(
                     typeof(DateTime),
                     new CodePrimitiveExpression(ticks),
                     new CodePropertyReferenceExpression(
                         new CodeTypeReferenceExpression(typeof(DateTimeKind)),
                         nameof(DateTimeKind.Utc)))
            };

            var property = new CodeMemberProperty()
            {
                Attributes = MemberAttributes.Assembly,
                Type = new CodeTypeReference(typeof(DateTime)),
                Name = name,
                HasGet = true,
                HasSet = false,
            };

            property.GetStatements.Add(
                new CodeMethodReturnStatement(
                    new CodeFieldReferenceExpression(
                        null,
                        fieldName)));

            yield return property;
        }

        private static CodeAttributeDeclaration DeclareAttribute(Type attributeType, params CodeAttributeArgument[] arguments)
        {
            var assemblyTypeReference = new CodeTypeReference(attributeType);
            return new CodeAttributeDeclaration(assemblyTypeReference, arguments);
        }

        private static CodeAttributeDeclaration DeclareAttribute(Type attributeType, params string[] arguments)
        {
            return DeclareAttribute(
                attributeType,
                arguments.Select(a => new CodeAttributeArgument(new CodePrimitiveExpression(a))).ToArray());
        }

#else

        public override bool Execute()
        {
            string fileContent = this.BuildCode();
            if (fileContent != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.OutputFile));
                Utilities.FileOperationWithRetry(() => File.WriteAllText(this.OutputFile, fileContent));
            }
            else
            {
                this.Log.LogError("CodeDomProvider not available for language: {0}. No version info will be embedded into assembly.", this.CodeLanguage);
            }

            return !this.Log.HasLoggedErrors;
        }

#endif

        public string BuildCode()
        {
            this.generator = this.CreateGenerator();
            if (this.generator != null)
            {
                this.generator.AddComment(FileHeaderComment);
                this.generator.AddBlankLine();
                this.generator.EmitNamespaceIfRequired(this.RootNamespace ?? "AssemblyInfo");
                this.GenerateAssemblyAttributes();

                if (this.EmitThisAssemblyClass)
                {
                    this.GenerateThisAssemblyClass();
                }

                return this.generator.GetCode();
            }

            return null;
        }

        private void GenerateAssemblyAttributes()
        {
            this.generator.DeclareAttribute(typeof(AssemblyVersionAttribute), this.AssemblyVersion);
            this.generator.DeclareAttribute(typeof(AssemblyFileVersionAttribute), this.AssemblyFileVersion);
            this.generator.DeclareAttribute(typeof(AssemblyInformationalVersionAttribute), this.AssemblyInformationalVersion);
            if (this.EmitNonVersionCustomAttributes)
            {
                if (!string.IsNullOrEmpty(this.AssemblyTitle))
                {
                    this.generator.DeclareAttribute(typeof(AssemblyTitleAttribute), this.AssemblyTitle);
                }

                if (!string.IsNullOrEmpty(this.AssemblyProduct))
                {
                    this.generator.DeclareAttribute(typeof(AssemblyProductAttribute), this.AssemblyProduct);
                }

                if (!string.IsNullOrEmpty(this.AssemblyCompany))
                {
                    this.generator.DeclareAttribute(typeof(AssemblyCompanyAttribute), this.AssemblyCompany);
                }

                if (!string.IsNullOrEmpty(this.AssemblyCopyright))
                {
                    this.generator.DeclareAttribute(typeof(AssemblyCopyrightAttribute), this.AssemblyCopyright);
                }
            }
        }

        private List<KeyValuePair<string, (object Value, bool EmitIfEmpty /* Only applies to string values */)>> GetFieldsForThisAssembly()
        {
            // Determine information about the public key used in the assembly name.
            string publicKey, publicKeyToken;
            bool hasKeyInfo = this.TryReadKeyInfo(out publicKey, out publicKeyToken);

            // Define the constants.
            var fields = new Dictionary<string, (object Value, bool EmitIfEmpty /* Only applies to string values */)>
                {
                    { "AssemblyVersion", (this.AssemblyVersion, false) },
                    { "AssemblyFileVersion", (this.AssemblyFileVersion, false) },
                    { "AssemblyInformationalVersion", (this.AssemblyInformationalVersion, false) },
                    { "AssemblyName", (this.AssemblyName, false) },
                    { "AssemblyTitle", (this.AssemblyTitle, false) },
                    { "AssemblyProduct", (this.AssemblyProduct, false) },
                    { "AssemblyCopyright", (this.AssemblyCopyright, false) },
                    { "AssemblyCompany", (this.AssemblyCompany, false) },
                    { "AssemblyConfiguration", (this.AssemblyConfiguration, false) },
                    { "GitCommitId", (this.GitCommitId, false) },
                    // These properties should be defined even if they are empty strings:
                    { "RootNamespace", (this.RootNamespace, true) },
                    // These non-string properties are always emitted:
                    { "IsPublicRelease", (this.PublicRelease, true) },
                    { "IsPrerelease", (!string.IsNullOrEmpty(this.PrereleaseVersion), true) },
                };

            if (hasKeyInfo)
            {
                fields.Add("PublicKey", (publicKey, false));
                fields.Add("PublicKeyToken", (publicKeyToken, false));
            }

            if (long.TryParse(this.GitCommitDateTicks, out long gitCommitDateTicks))
            {
                fields.Add("GitCommitDate", (gitCommitDateTicks, true));
            }

            if (this.AdditionalThisAssemblyFields != null && this.AdditionalThisAssemblyFields.Length > 0)
            {
                foreach (var item in this.AdditionalThisAssemblyFields)
                {
                    if (item == null)
                        continue;

                    var name = item.ItemSpec.Trim();
                    var metaClone = item.CloneCustomMetadata();
                    var meta = new Dictionary<string, string>(metaClone.Count, StringComparer.OrdinalIgnoreCase);
                    var iter = metaClone.GetEnumerator();

                    while ( iter.MoveNext() )
                    {
                        meta.Add((string)iter.Key, (string)iter.Value);
                    }

                    object value = null;
                    bool emitIfEmpty = false;

                    if (meta.TryGetValue("String", out var stringValue))
                    {
                        value = stringValue;
                        if (meta.TryGetValue("EmitIfEmpty", out var emitIfEmptyString))
                        {
                            if (!bool.TryParse(emitIfEmptyString, out emitIfEmpty))
                            {
                                this.Log.LogError("The value '{0}' for EmitIfEmpty metadata for item '{1}' in AdditionalThisAssemblyFields is not valid.", emitIfEmptyString, name);
                                continue;
                            }
                        }
                    }

                    if (meta.TryGetValue("Boolean", out var boolText))
                    {
                        if (value != null)
                        {
                            this.Log.LogError("The metadata for item '{0}' in AdditionalThisAssemblyFields specifies more than one kind of value.", name);
                            continue;
                        }

                        if (bool.TryParse(boolText, out var boolValue))
                        {
                            value = boolValue;
                        }
                        else
                        {
                            this.Log.LogError("The Boolean value '{0}' for item '{1}' in AdditionalThisAssemblyFields is not valid.", boolText, name);
                            continue;
                        }
                    }

                    if (meta.TryGetValue("Ticks", out var ticksText))
                    {
                        if (value != null)
                        {
                            this.Log.LogError("The metadata for item '{0}' in AdditionalThisAssemblyFields specifies more than one kind of value.", name);
                            continue;
                        }

                        if (long.TryParse(ticksText, out var ticksValue))
                        {
                            value = ticksValue;
                        }
                        else
                        {
                            this.Log.LogError("The Ticks value '{0}' for item '{1}' in AdditionalThisAssemblyFields is not valid.", ticksText, name);
                            continue;
                        }
                    }

                    if ( value == null )
                    {
                        this.Log.LogWarning("Field '{0}' in AdditionalThisAssemblyFields has no value and will be ignored.", name);
                        continue;
                    }

                    if (fields.ContainsKey(name))
                    {
                        this.Log.LogError("Field name '{0}' in AdditionalThisAssemblyFields has already been defined.", name);
                        continue;
                    }

                    fields.Add(name, (value, emitIfEmpty));
                }
            }

            return fields.OrderBy(f => f.Key).ToList();
        }

        private void GenerateThisAssemblyClass()
        {
            this.generator.StartThisAssemblyClass();

            var fields = this.GetFieldsForThisAssembly();

            foreach (var pair in fields)
            {
                switch (pair.Value.Value)
                {
                    case null:
                        if (pair.Value.EmitIfEmpty)
                        {
                            this.generator.AddThisAssemblyMember(pair.Key, string.Empty);
                        }
                        break;

                    case string stringValue:
                        if (pair.Value.EmitIfEmpty || !string.IsNullOrEmpty(stringValue))
                        {
                            this.generator.AddThisAssemblyMember(pair.Key, stringValue);
                        }
                        break;

                    case bool boolValue:
                        this.generator.AddThisAssemblyMember(pair.Key, boolValue);
                        break;

                    case long ticksValue:
                        this.generator.AddThisAssemblyMember(pair.Key, ticksValue);
                        break;

                    default:
                        throw new NotImplementedException();
                }
            }

            this.generator.EndThisAssemblyClass();
        }

        private CodeGenerator CreateGenerator()
        {
            switch (this.CodeLanguage.ToLowerInvariant())
            {
                case "c#":
                    return new CSharpCodeGenerator();
                case "visual basic":
                case "visualbasic":
                case "vb":
                    return new VisualBasicCodeGenerator();
                case "f#":
                    return new FSharpCodeGenerator();
                default:
                    return null;
            }
        }

        private abstract class CodeGenerator
        {
            protected readonly StringBuilder codeBuilder;

            internal CodeGenerator()
            {
                this.codeBuilder = new StringBuilder();
            }

            internal abstract void AddComment(string comment);

            internal abstract void DeclareAttribute(Type type, string arg);

            internal abstract void StartThisAssemblyClass();

            internal abstract void AddThisAssemblyMember(string name, string value);

            internal abstract void AddThisAssemblyMember(string name, bool value);

            internal abstract void AddThisAssemblyMember(string name, long ticks);

            internal abstract void EndThisAssemblyClass();

            /// <summary>
            /// Gives languages that *require* a namespace a chance to emit such.
            /// </summary>
            /// <param name="ns">The RootNamespace of the project.</param>
            internal virtual void EmitNamespaceIfRequired(string ns) { }

            internal string GetCode() => this.codeBuilder.ToString();

            internal void AddBlankLine()
            {
                this.codeBuilder.AppendLine();
            }

            protected void AddCodeComment(string comment, string token)
            {
                var sr = new StringReader(comment);
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    this.codeBuilder.Append(token);
                    this.codeBuilder.AppendLine(line);
                }
            }
        }

        private class FSharpCodeGenerator : CodeGenerator
        {
            internal override void AddComment(string comment)
            {
                this.AddCodeComment(comment, "//");
            }

            internal override void AddThisAssemblyMember(string name, string value)
            {
                this.codeBuilder.AppendLine($"  static member internal {name} = \"{value}\"");
            }

            internal override void AddThisAssemblyMember(string name, bool value)
            {
                this.codeBuilder.AppendLine($"  static member internal {name} = {(value ? "true" : "false")}");
            }

            internal override void AddThisAssemblyMember(string name, long ticks)
            {
                this.codeBuilder.AppendLine($"  static member internal {name} = new System.DateTime({ticks}L, System.DateTimeKind.Utc)");
            }

            internal override void EmitNamespaceIfRequired(string ns)
            {
                this.codeBuilder.AppendLine($"namespace {ns}");
            }

            internal override void DeclareAttribute(Type type, string arg)
            {
                this.codeBuilder.AppendLine($"[<assembly: {type.FullName}(\"{arg}\")>]");
            }

            internal override void EndThisAssemblyClass()
            {
                this.codeBuilder.AppendLine("do()");
            }

            internal override void StartThisAssemblyClass()
            {
                this.codeBuilder.AppendLine("do()");
                this.codeBuilder.AppendLine($"#if {CompilerDefinesAroundGeneratedCodeAttribute}");
                this.codeBuilder.AppendLine($"[<System.CodeDom.Compiler.GeneratedCode(\"{GeneratorName}\",\"{GeneratorVersion}\")>]");
                this.codeBuilder.AppendLine("#endif");
                this.codeBuilder.AppendLine($"#if {CompilerDefinesAroundExcludeFromCodeCoverageAttribute}");
                this.codeBuilder.AppendLine("[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]");
                this.codeBuilder.AppendLine("#endif");
                this.codeBuilder.AppendLine("type internal ThisAssembly() =");
            }
        }

        private class CSharpCodeGenerator : CodeGenerator
        {
            internal override void AddComment(string comment)
            {
                this.AddCodeComment(comment, "//");
            }

            internal override void DeclareAttribute(Type type, string arg)
            {
                this.codeBuilder.AppendLine($"[assembly: {type.FullName}(\"{arg}\")]");
            }

            internal override void StartThisAssemblyClass()
            {
                this.codeBuilder.AppendLine($"#if {CompilerDefinesAroundGeneratedCodeAttribute}");
                this.codeBuilder.AppendLine($"[System.CodeDom.Compiler.GeneratedCode(\"{GeneratorName}\",\"{GeneratorVersion}\")]");
                this.codeBuilder.AppendLine("#endif");
                this.codeBuilder.AppendLine($"#if {CompilerDefinesAroundExcludeFromCodeCoverageAttribute}");
                this.codeBuilder.AppendLine("[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
                this.codeBuilder.AppendLine("#endif");
                this.codeBuilder.AppendLine("internal static partial class ThisAssembly {");
            }

            internal override void AddThisAssemblyMember(string name, string value)
            {
                this.codeBuilder.AppendLine($"    internal const string {name} = \"{value}\";");
            }

            internal override void AddThisAssemblyMember(string name, bool value)
            {
                this.codeBuilder.AppendLine($"    internal const bool {name} = {(value ? "true" : "false")};");
            }

            internal override void AddThisAssemblyMember(string name, long ticks)
            {
                this.codeBuilder.AppendLine($"    internal static readonly System.DateTime {name} = new System.DateTime({ticks}L, System.DateTimeKind.Utc);");
            }

            internal override void EndThisAssemblyClass()
            {
                this.codeBuilder.AppendLine("}");
            }
        }

        private class VisualBasicCodeGenerator : CodeGenerator
        {
            internal override void AddComment(string comment)
            {
                this.AddCodeComment(comment, "'");
            }

            internal override void DeclareAttribute(Type type, string arg)
            {
                this.codeBuilder.AppendLine($"<Assembly: {type.FullName}(\"{arg}\")>");
            }

            internal override void StartThisAssemblyClass()
            {
                this.codeBuilder.AppendLine($"#If {CompilerDefinesAroundExcludeFromCodeCoverageAttribute.Replace("||", " Or ")} Then");
                this.codeBuilder.AppendLine($"<System.CodeDom.Compiler.GeneratedCode(\"{GeneratorName}\",\"{GeneratorVersion}\")>");
                this.codeBuilder.AppendLine("<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>");
                this.codeBuilder.AppendLine("Partial Friend NotInheritable Class ThisAssembly");
                this.codeBuilder.AppendLine($"#ElseIf {CompilerDefinesAroundGeneratedCodeAttribute.Replace("||", " Or ")} Then");
                this.codeBuilder.AppendLine($"<System.CodeDom.Compiler.GeneratedCode(\"{GeneratorName}\",\"{GeneratorVersion}\")>");
                this.codeBuilder.AppendLine("Partial Friend NotInheritable Class ThisAssembly");
                this.codeBuilder.AppendLine("#Else");
                this.codeBuilder.AppendLine("Partial Friend NotInheritable Class ThisAssembly");
                this.codeBuilder.AppendLine("#End If");
            }

            internal override void AddThisAssemblyMember(string name, string value)
            {
                this.codeBuilder.AppendLine($"    Friend Const {name} As String = \"{value}\"");
            }

            internal override void AddThisAssemblyMember(string name, bool value)
            {
                this.codeBuilder.AppendLine($"    Friend Const {name} As Boolean = {(value ? "True" : "False")}");
            }

            internal override void AddThisAssemblyMember(string name, long ticks)
            {
                this.codeBuilder.AppendLine($"    Friend Shared ReadOnly {name} As System.DateTime = New System.DateTime({ticks}L, System.DateTimeKind.Utc)");
            }

            internal override void EndThisAssemblyClass()
            {
                this.codeBuilder.AppendLine("End Class");
            }
        }

        private static string ToHex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Gets the public key from a key container.
        /// </summary>
        /// <param name="containerName">The name of the container.</param>
        /// <returns>The public key.</returns>
        private static byte[] GetPublicKeyFromKeyContainer(string containerName)
        {
            throw new NotImplementedException();
        }

        private static byte[] GetPublicKeyFromKeyPair(byte[] keyPair)
        {
            byte[] publicKey;
            if (CryptoBlobParser.TryGetPublicKeyFromPrivateKeyBlob(keyPair, out publicKey))
            {
                return publicKey;
            }
            else
            {
                throw new ArgumentException("Invalid keypair");
            }
        }

        private bool TryReadKeyInfo(out string publicKey, out string publicKeyToken)
        {
            try
            {
                byte[] publicKeyBytes = null;
                if (!string.IsNullOrEmpty(this.AssemblyOriginatorKeyFile) && File.Exists(this.AssemblyOriginatorKeyFile))
                {
                    if (Path.GetExtension(this.AssemblyOriginatorKeyFile).Equals(".snk", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] keyBytes = File.ReadAllBytes(this.AssemblyOriginatorKeyFile);
                        bool publicKeyOnly = keyBytes[0] != 0x07;
                        publicKeyBytes = publicKeyOnly ? keyBytes : GetPublicKeyFromKeyPair(keyBytes);
                    }
                }
                else if (!string.IsNullOrEmpty(this.AssemblyKeyContainerName))
                {
                    publicKeyBytes = GetPublicKeyFromKeyContainer(this.AssemblyKeyContainerName);
                }

                if (publicKeyBytes != null && publicKeyBytes.Length > 0) // If .NET 2.0 isn't installed, we get byte[0] back.
                {
                    publicKey = ToHex(publicKeyBytes);
                    publicKeyToken = ToHex(CryptoBlobParser.GetStrongNameTokenFromPublicKey(publicKeyBytes));
                }
                else
                {
                    if (publicKeyBytes != null)
                    {
                        this.Log.LogWarning("Unable to emit public key fields in ThisAssembly class because .NET 2.0 isn't installed.");
                    }

                    publicKey = null;
                    publicKeyToken = null;
                    return false;
                }

                return true;
            }
            catch (NotImplementedException)
            {
                publicKey = null;
                publicKeyToken = null;
                return false;
            }
        }
    }
}
