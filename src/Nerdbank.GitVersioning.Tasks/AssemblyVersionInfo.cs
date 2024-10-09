﻿// Copyright (c) .NET Foundation and Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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
using Validation;

namespace Nerdbank.GitVersioning.Tasks
{
    public class AssemblyVersionInfo : Microsoft.Build.Utilities.Task
    {
        public static readonly string GeneratorName = ThisAssembly.AssemblyName;
        public static readonly string GeneratorVersion = ThisAssembly.AssemblyVersion;

        /// <summary>
        /// The #if expression that surrounds a <see cref="GeneratedCodeAttribute"/> to avoid a compilation failure when targeting the nano framework.
        /// </summary>
        /// <see href="https://github.com/dotnet/Nerdbank.GitVersioning/issues/346" />
        private const string CompilerDefinesAroundGeneratedCodeAttribute = "NETSTANDARD || NETFRAMEWORK || NETCOREAPP";
        private const string CompilerDefinesAroundExcludeFromCodeCoverageAttribute = "NET40_OR_GREATER || NETCOREAPP2_0_OR_GREATER || NETSTANDARD2_0_OR_GREATER";
        private const string FileHeaderComment = @"------------------------------------------------------------------------------
 <auto-generated>
     This code was generated by a tool.
     Runtime Version:4.0.30319.42000

     Changes to this file may cause incorrect behavior and will be lost if
     the code is regenerated.
 </auto-generated>
------------------------------------------------------------------------------
";

#if NETFRAMEWORK
        private static readonly CodeGeneratorOptions CodeGeneratorOptions = new CodeGeneratorOptions
        {
            BlankLinesBetweenMembers = false,
            IndentString = "    ",
        };

        private CodeCompileUnit generatedFile;
#endif

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

        public string ThisAssemblyNamespace { get; set; }

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

        public string GitCommitAuthorDateTicks { get; set; }

        public bool EmitThisAssemblyClass { get; set; } = true;

        /// <summary>
        /// Gets or sets the additional fields to be added to the <c>ThisAssembly</c> class.
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

        public string BuildCode()
        {
            this.generator = this.CreateGenerator(this.ThisAssemblyNamespace, this.RootNamespace);

            if (this.generator is object)
            {
                this.generator.AddComment(FileHeaderComment);
                this.generator.AddBlankLine();
                this.generator.AddAnalysisSuppressions();
                this.generator.AddBlankLine();

                this.GenerateAssemblyAttributes();

                if (this.EmitThisAssemblyClass)
                {
                    this.GenerateThisAssemblyClass();
                }

                return this.generator.GetCode();
            }

            return null;
        }

#if NETFRAMEWORK
        /// <inheritdoc/>
        public override bool Execute()
        {
            // attempt to use local codegen
            string fileContent = this.BuildCode();
            if (fileContent is object)
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
                            codeDomProvider.GenerateCodeFromCompileUnit(this.generatedFile, fileWriter, CodeGeneratorOptions);
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
#endif

#if !NETFRAMEWORK
        /// <inheritdoc/>
        public override bool Execute()
        {
            string fileContent = this.BuildCode();
            if (fileContent is object)
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

        private static string ToHex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", string.Empty).ToLowerInvariant();
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

#if NETFRAMEWORK
        private static CodeMemberField CreateField<T>(string name, T value)
        {
            return new CodeMemberField(typeof(T), name)
            {
                Attributes = MemberAttributes.Const | MemberAttributes.Assembly,
                InitExpression = new CodePrimitiveExpression(value),
            };
        }

        private static IEnumerable<CodeTypeMember> CreateDateTimeField(string name, DateTime value)
        {
            Requires.NotNullOrEmpty(name, nameof(name));

            ////internal static global::System.DateTime GitCommitDate => new global::System.DateTime({ticks}, global::System.DateTimeKind.Utc);");

            var property = new CodeMemberProperty()
            {
                Attributes = MemberAttributes.Assembly | MemberAttributes.Static | MemberAttributes.Final,
                Type = new CodeTypeReference(typeof(DateTime)),
                Name = name,
                HasGet = true,
                HasSet = false,
            };

            property.GetStatements.Add(
                new CodeMethodReturnStatement(
                    new CodeObjectCreateExpression(
                     typeof(DateTime),
                     new CodePrimitiveExpression(value.Ticks),
                     new CodePropertyReferenceExpression(
                         new CodeTypeReferenceExpression(typeof(DateTimeKind)),
                         nameof(DateTimeKind.Utc)))));

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

        private CodeTypeDeclaration CreateThisAssemblyClass()
        {
            var thisAssembly = new CodeTypeDeclaration("ThisAssembly")
            {
                IsClass = true,
                IsPartial = true,
                TypeAttributes = TypeAttributes.NotPublic | TypeAttributes.Sealed,
            };

            var codeAttributeDeclarationCollection = new CodeAttributeDeclarationCollection();
            codeAttributeDeclarationCollection.Add(new CodeAttributeDeclaration(
                "global::System.CodeDom.Compiler.GeneratedCode",
                new CodeAttributeArgument(new CodePrimitiveExpression(GeneratorName)),
                new CodeAttributeArgument(new CodePrimitiveExpression(GeneratorVersion))));
            thisAssembly.CustomAttributes = codeAttributeDeclarationCollection;

            // CodeDOM doesn't support static classes, so hide the constructor instead.
            thisAssembly.Members.Add(new CodeConstructor { Attributes = MemberAttributes.Private });

            List<KeyValuePair<string, (object Value, bool EmitIfEmpty)>> fields = this.GetFieldsForThisAssembly();

            foreach (KeyValuePair<string, (object Value, bool EmitIfEmpty)> pair in fields)
            {
                switch (pair.Value.Value)
                {
                    case null:
                        if (pair.Value.EmitIfEmpty)
                        {
                            thisAssembly.Members.Add(CreateField(pair.Key, (string)null));
                        }

                        break;
                    case string stringValue:
                        if (pair.Value.EmitIfEmpty || !string.IsNullOrEmpty(stringValue))
                        {
                            thisAssembly.Members.Add(CreateField(pair.Key, stringValue));
                        }

                        break;

                    case bool boolValue:
                        thisAssembly.Members.Add(CreateField(pair.Key, boolValue));
                        break;

                    case DateTime dateValue:
                        thisAssembly.Members.AddRange(CreateDateTimeField(pair.Key, dateValue).ToArray());
                        break;

                    default:
                        throw new NotSupportedException($"Value type {pair.Value.Value.GetType().Name} as found for the \"{pair.Key}\" property is not supported.");
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
#endif

        private void GenerateAssemblyAttributes()
        {
            this.generator.StartAssemblyAttributes();
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

            this.generator.EndAssemblyAttributes();
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
                fields.Add("GitCommitDate", (new DateTime(gitCommitDateTicks, DateTimeKind.Utc), true));
            }

            if (long.TryParse(this.GitCommitAuthorDateTicks, out long gitCommitAuthorDateTicks))
            {
                fields.Add("GitCommitAuthorDate", (new DateTime(gitCommitAuthorDateTicks, DateTimeKind.Utc), true));
            }

            if (this.AdditionalThisAssemblyFields is object)
            {
                foreach (ITaskItem item in this.AdditionalThisAssemblyFields)
                {
                    string name = item.ItemSpec.Trim();
                    var meta = new Dictionary<string, string>(item.MetadataCount, StringComparer.OrdinalIgnoreCase);
                    foreach (string metadataName in item.MetadataNames)
                    {
                        meta.Add(metadataName, item.GetMetadata(metadataName));
                    }

                    object value = null;
                    bool emitIfEmpty = false;

                    if (meta.TryGetValue("String", out string stringValue))
                    {
                        value = stringValue;
                        if (meta.TryGetValue("EmitIfEmpty", out string emitIfEmptyString))
                        {
                            if (!bool.TryParse(emitIfEmptyString, out emitIfEmpty))
                            {
                                this.Log.LogError($"The value '{emitIfEmptyString}' for EmitIfEmpty metadata for item '{name}' in {nameof(this.AdditionalThisAssemblyFields)} is not valid.");
                                continue;
                            }
                        }
                    }

                    if (meta.TryGetValue("Boolean", out string boolText))
                    {
                        if (value is object)
                        {
                            this.Log.LogError($"The metadata for item '{name}' in {nameof(this.AdditionalThisAssemblyFields)} specifies more than one kind of value.");
                            continue;
                        }

                        if (bool.TryParse(boolText, out bool boolValue))
                        {
                            value = boolValue;
                        }
                        else
                        {
                            this.Log.LogError($"The Boolean value '{boolText}' for item '{name}' in AdditionalThisAssemblyFields is not valid.");
                            continue;
                        }
                    }

                    if (meta.TryGetValue("Ticks", out string ticksText))
                    {
                        if (value is object)
                        {
                            this.Log.LogError($"The metadata for item '{name}' in {nameof(this.AdditionalThisAssemblyFields)} specifies more than one kind of value.");
                            continue;
                        }

                        if (long.TryParse(ticksText, out long ticksValue))
                        {
                            value = new DateTime(ticksValue, DateTimeKind.Utc);
                        }
                        else
                        {
                            this.Log.LogError($"The Ticks value '{ticksText}' for item '{name}' in {nameof(this.AdditionalThisAssemblyFields)} is not valid.");
                            continue;
                        }
                    }

                    if (value is null)
                    {
                        this.Log.LogWarning($"Field '{name}' in {nameof(this.AdditionalThisAssemblyFields)} has no value and will be ignored.");
                        continue;
                    }

                    if (fields.ContainsKey(name))
                    {
                        this.Log.LogError($"Field name '{name}' in {nameof(this.AdditionalThisAssemblyFields)} is defined multiple times.");
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

            List<KeyValuePair<string, (object Value, bool EmitIfEmpty)>> fields = this.GetFieldsForThisAssembly();

            foreach (KeyValuePair<string, (object Value, bool EmitIfEmpty)> pair in fields)
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

                    case DateTime datetimeValue:
                        this.generator.AddThisAssemblyMember(pair.Key, datetimeValue);
                        break;

                    default:
                        throw new NotSupportedException($"Value type {pair.Value.Value.GetType().Name} as found for the \"{pair.Key}\" property is not supported.");
                }
            }

            this.generator.EndThisAssemblyClass();
        }

        private CodeGenerator CreateGenerator(string thisAssemblyNamespace, string rootNamespace)
        {
            // The C#/VB generators did not emit namespaces in past versions of NB.GV, so for compatibility, only check the
            // new ThisAssemblyNamespace property for these.
            var userNs = !string.IsNullOrEmpty(thisAssemblyNamespace) ? thisAssemblyNamespace : null;

            switch (this.CodeLanguage.ToLowerInvariant())
            {
                case "c#":
                    return new CSharpCodeGenerator(userNs);
                case "visual basic":
                case "visualbasic":
                case "vb":
                    return new VisualBasicCodeGenerator(userNs);
                case "f#":
                    // The F# generator must emit a namespace, so it respects both ThisAssemblyNamespace and RootNamespace.
                    return new FSharpCodeGenerator(userNs ?? (!string.IsNullOrEmpty(rootNamespace) ? rootNamespace : "AssemblyInfo"));
                default:
                    return null;
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

                // If .NET 2.0 isn't installed, we get byte[0] back.
                if (publicKeyBytes is object && publicKeyBytes.Length > 0)
                {
                    publicKey = ToHex(publicKeyBytes);
                    publicKeyToken = ToHex(CryptoBlobParser.GetStrongNameTokenFromPublicKey(publicKeyBytes));
                }
                else
                {
                    if (publicKeyBytes is object)
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

        private abstract class CodeGenerator
        {
            internal CodeGenerator(string ns)
            {
                this.CodeBuilder = new StringBuilder();
                this.Namespace = ns;
            }

            protected StringBuilder CodeBuilder { get; }

            protected string Namespace { get; }

            protected virtual IEnumerable<string> WarningCodesToSuppress { get; } = new string[]
            {
                "CA2243", // Attribute string literals should parse correctly
            };

            internal abstract void AddAnalysisSuppressions();

            internal abstract void AddComment(string comment);

            internal virtual void StartAssemblyAttributes()
            {
            }

            internal virtual void EndAssemblyAttributes()
            {
            }

            internal abstract void DeclareAttribute(Type type, string arg);

            internal abstract void StartThisAssemblyClass();

            internal abstract void AddThisAssemblyMember(string name, string value);

            internal abstract void AddThisAssemblyMember(string name, bool value);

            internal abstract void AddThisAssemblyMember(string name, DateTime value);

            internal abstract void EndThisAssemblyClass();

            internal string GetCode() => this.CodeBuilder.ToString();

            internal void AddBlankLine()
            {
                this.CodeBuilder.AppendLine();
            }

            protected void AddCodeComment(string comment, string token)
            {
                var sr = new StringReader(comment);
                string line;
                while ((line = sr.ReadLine()) is object)
                {
                    this.CodeBuilder.Append(token);
                    this.CodeBuilder.AppendLine(line);
                }
            }
        }

        private class FSharpCodeGenerator : CodeGenerator
        {
            public FSharpCodeGenerator(string ns)
                : base(ns)
            {
            }

            internal override void AddAnalysisSuppressions()
            {
                this.CodeBuilder.AppendLine($"#nowarn {string.Join(" ", this.WarningCodesToSuppress.Select(c => $"\"{c}\""))}");
            }

            internal override void AddComment(string comment)
            {
                this.AddCodeComment(comment, "//");
            }

            internal override void AddThisAssemblyMember(string name, string value)
            {
                this.CodeBuilder.AppendLine($"  static member internal {name} = \"{value}\"");
            }

            internal override void AddThisAssemblyMember(string name, bool value)
            {
                this.CodeBuilder.AppendLine($"  static member internal {name} = {(value ? "true" : "false")}");
            }

            internal override void AddThisAssemblyMember(string name, DateTime value)
            {
                this.CodeBuilder.AppendLine($"  static member internal {name} = new global.System.DateTime({value.Ticks}L, global.System.DateTimeKind.Utc)");
            }

            internal override void StartAssemblyAttributes()
            {
                this.CodeBuilder.AppendLine($"namespace {this.Namespace}");
            }

            internal override void EndAssemblyAttributes()
            {
                this.CodeBuilder.AppendLine("do()");
            }

            internal override void DeclareAttribute(Type type, string arg)
            {
                this.CodeBuilder.AppendLine($"[<assembly: global.{type.FullName}(\"{arg}\")>]");
            }

            internal override void EndThisAssemblyClass()
            {
                this.CodeBuilder.AppendLine("do()");
            }

            internal override void StartThisAssemblyClass()
            {
                this.CodeBuilder.AppendLine($"#if {CompilerDefinesAroundGeneratedCodeAttribute}");
                this.CodeBuilder.AppendLine($"[<global.System.CodeDom.Compiler.GeneratedCode(\"{GeneratorName}\",\"{GeneratorVersion}\")>]");
                this.CodeBuilder.AppendLine("#endif");
                this.CodeBuilder.AppendLine($"#if {CompilerDefinesAroundExcludeFromCodeCoverageAttribute}");
                this.CodeBuilder.AppendLine("[<global.System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]");
                this.CodeBuilder.AppendLine("#endif");
                this.CodeBuilder.AppendLine("type internal ThisAssembly() =");
            }
        }

        private class CSharpCodeGenerator : CodeGenerator
        {
            public CSharpCodeGenerator(string ns)
                : base(ns)
            {
            }

            internal override void AddAnalysisSuppressions()
            {
                this.CodeBuilder.AppendLine($"#pragma warning disable {string.Join(", ", this.WarningCodesToSuppress)}");
            }

            internal override void AddComment(string comment)
            {
                this.AddCodeComment(comment, "//");
            }

            internal override void DeclareAttribute(Type type, string arg)
            {
                this.CodeBuilder.AppendLine($"[assembly: global::{type.FullName}(\"{arg}\")]");
            }

            internal override void StartThisAssemblyClass()
            {
                if (this.Namespace is { } ns)
                {
                    this.CodeBuilder.AppendLine($"namespace {ns} {{");
                }

                this.CodeBuilder.AppendLine($"#if {CompilerDefinesAroundGeneratedCodeAttribute}");
                this.CodeBuilder.AppendLine($"[global::System.CodeDom.Compiler.GeneratedCode(\"{GeneratorName}\",\"{GeneratorVersion}\")]");
                this.CodeBuilder.AppendLine("#endif");
                this.CodeBuilder.AppendLine($"#if {CompilerDefinesAroundExcludeFromCodeCoverageAttribute}");
                this.CodeBuilder.AppendLine("[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
                this.CodeBuilder.AppendLine("#endif");
                this.CodeBuilder.AppendLine("internal static partial class ThisAssembly {");
            }

            internal override void AddThisAssemblyMember(string name, string value)
            {
                this.CodeBuilder.AppendLine($"    internal const string {name} = \"{value}\";");
            }

            internal override void AddThisAssemblyMember(string name, bool value)
            {
                this.CodeBuilder.AppendLine($"    internal const bool {name} = {(value ? "true" : "false")};");
            }

            internal override void AddThisAssemblyMember(string name, DateTime value)
            {
                this.CodeBuilder.AppendLine($"    internal static readonly global::System.DateTime {name} = new global::System.DateTime({value.Ticks}L, global::System.DateTimeKind.Utc);");
            }

            internal override void EndThisAssemblyClass()
            {
                this.CodeBuilder.AppendLine("}");

                if (this.Namespace is not null)
                {
                    this.CodeBuilder.AppendLine("}");
                }
            }
        }

        private class VisualBasicCodeGenerator : CodeGenerator
        {
            public VisualBasicCodeGenerator(string ns)
                : base(ns)
            {
            }

            internal override void AddAnalysisSuppressions()
            {
                this.CodeBuilder.AppendLine($"#Disable Warning {string.Join(", ", this.WarningCodesToSuppress)}");
            }

            internal override void AddComment(string comment)
            {
                this.AddCodeComment(comment, "'");
            }

            internal override void DeclareAttribute(Type type, string arg)
            {
                this.CodeBuilder.AppendLine($"<Assembly: Global.{type.FullName}(\"{arg}\")>");
            }

            internal override void StartThisAssemblyClass()
            {
                if (this.Namespace is { } ns)
                {
                    this.CodeBuilder.AppendLine($"Namespace {ns}");
                }

                this.CodeBuilder.AppendLine($"#If {CompilerDefinesAroundExcludeFromCodeCoverageAttribute.Replace("||", " Or ")} Then");
                this.CodeBuilder.AppendLine($"<Global.System.CodeDom.Compiler.GeneratedCode(\"{GeneratorName}\",\"{GeneratorVersion}\")>");
                this.CodeBuilder.AppendLine("<Global.System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>");
                this.CodeBuilder.AppendLine("Partial Friend NotInheritable Class ThisAssembly");
                this.CodeBuilder.AppendLine($"#ElseIf {CompilerDefinesAroundGeneratedCodeAttribute.Replace("||", " Or ")} Then");
                this.CodeBuilder.AppendLine($"<Global.System.CodeDom.Compiler.GeneratedCode(\"{GeneratorName}\",\"{GeneratorVersion}\")>");
                this.CodeBuilder.AppendLine("Partial Friend NotInheritable Class ThisAssembly");
                this.CodeBuilder.AppendLine("#Else");
                this.CodeBuilder.AppendLine("Partial Friend NotInheritable Class ThisAssembly");
                this.CodeBuilder.AppendLine("#End If");
            }

            internal override void AddThisAssemblyMember(string name, string value)
            {
                this.CodeBuilder.AppendLine($"    Friend Const {name} As String = \"{value}\"");
            }

            internal override void AddThisAssemblyMember(string name, bool value)
            {
                this.CodeBuilder.AppendLine($"    Friend Const {name} As Boolean = {(value ? "True" : "False")}");
            }

            internal override void AddThisAssemblyMember(string name, DateTime value)
            {
                this.CodeBuilder.AppendLine($"    Friend Shared ReadOnly {name} As Global.System.DateTime = New Global.System.DateTime({value.Ticks}L, Global.System.DateTimeKind.Utc)");
            }

            internal override void EndThisAssemblyClass()
            {
                this.CodeBuilder.AppendLine("End Class");

                if (this.Namespace is not null)
                {
                    this.CodeBuilder.AppendLine("End Namespace");
                }
            }
        }
    }
}
