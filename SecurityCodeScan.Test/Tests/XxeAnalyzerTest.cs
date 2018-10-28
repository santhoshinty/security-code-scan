﻿using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Web.XmlTransform;
using SecurityCodeScan.Analyzers;
using SecurityCodeScan.Test.Helpers;

namespace SecurityCodeScan.Test.XXE
{
    [TestClass]
    public class XxeAnalyzerTest : XxeAnalyzerTestBase
    {
        protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers(string language)
        {
            return new DiagnosticAnalyzer[] { new XxeDiagnosticAnalyzerCSharp(), new XxeDiagnosticAnalyzerVisualBasic() };
        }

        private static readonly PortableExecutableReference[] References =
        {
            MetadataReference.CreateFromFile(typeof(XmlFileInfoDocument).Assembly.Location),
        };

        /// <summary> XML parsing vulnerable to XXE </summary>
        private DiagnosticResult[] Expected = new[]
        {
            new DiagnosticResult { Id = "SCS0007", Severity = DiagnosticSeverity.Warning }
        };

        protected override IEnumerable<MetadataReference> GetAdditionalReferences() => References;

        [TestCategory("Detect")]
        [TestMethod]
        public async Task XPathDocument()
        {
            const string cSharpTest = @"
using System.Xml.XPath;

class Xxe
{
    public static void parseUpload(string path)
    {
        var document = new XPathDocument(path);
        var nav = document.CreateNavigator();
    }
}";

            const string visualBasicTest = @"
Imports System.Xml.XPath

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim document As New XPathDocument(path)
        Dim nav As XPathNavigator = document.CreateNavigator()
    End Sub
End Class
";

            await VerifyDiagnosticSafeUnsafe(cSharpTest, visualBasicTest, Expected).ConfigureAwait(false);

            const string cSharpTest2 = @"
using System.Xml.XPath;

class Xxe
{
    public static void parseUpload(string path)
    {
        new XPathDocument(path).CreateNavigator();
    }
}";

            const string visualBasicTest2 = @"
Imports System.Xml.XPath

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Call New XPathDocument(path).CreateNavigator()
    End Sub
End Class
";

            await VerifyDiagnosticSafeUnsafe(cSharpTest2, visualBasicTest2, Expected).ConfigureAwait(false);
        }

        [TestCategory("Detect")]
        [DataRow("new StringReader(path)", Warnings.OnFramework)]
        [DataRow("XmlReader.Create (path, new XmlReaderSettings (){DtdProcessing = DtdProcessing.Parse})", Warnings.OnFramework)]
        [DataTestMethod]
        public async Task XmlSchemaRead(string createReader, Warnings expectWarnings)
        {
            string vbCreateReader = createReader.Replace("(){", "() With {.");

            var cSharpTest = $@"
#pragma warning disable 8019
    using System.Xml.Schema;
    using System.IO;
    using System.Xml;
#pragma warning restore 8019

class Xxe
{{
    public static void parseUpload(string path)
    {{
        var tr = {createReader};
        var schema = XmlSchema.Read(tr, null);
    }}
}}";

            var visualBasicTest = $@"
#Disable Warning BC50001
    Imports System.Xml.Schema
    Imports System.IO
    Imports System.Xml
#Enable Warning BC50001

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim tr = {vbCreateReader}
        Dim schema = XmlSchema.Read(tr, Nothing)
    End Sub
End Class
";
            await Verify(cSharpTest, visualBasicTest, Expected, expectWarnings).ConfigureAwait(false);

            string cSharpTest2 = $@"
#pragma warning disable 8019
    using System.Xml.Schema;
    using System.IO;
    using System.Xml;
#pragma warning restore 8019

class Xxe
{{
    public static void parseUpload(string path)
    {{
        XmlSchema.Read({createReader}, null);
    }}
}}";

            var visualBasicTest2 = $@"
#Disable Warning BC50001
    Imports System.Xml.Schema
    Imports System.IO
    Imports System.Xml
#Enable Warning BC50001

Class Xxe
    Public Shared Sub parseUpload(path As String)
        XmlSchema.Read({vbCreateReader}, Nothing)
    End Sub
End Class
";
            await Verify(cSharpTest2, visualBasicTest2, Expected, expectWarnings).ConfigureAwait(false);
        }

        [TestCategory("Detect")]
        [DataRow("XmlDocument",        "Load(text)",      Warnings.OnFramework)]
        [DataRow("XmlDocument",        "LoadXml(text)",   Warnings.OnFramework)]
        [DataRow("XmlDocument",        "InnerXml = text", Warnings.OnFramework)]
        [DataRow("ConfigXmlDocument",  "Load(text)",      Warnings.OnFramework)]
        [DataRow("ConfigXmlDocument",  "LoadXml(text)",   Warnings.OnFramework)]
        [DataRow("XmlDataDocument",    "Load(text)",      Warnings.OnFramework)]
        [DataRow("XmlDataDocument",    "LoadXml(text)",   Warnings.OnFramework)]
        [DataRow("DerivedXmlDocument", "Load(text)",      Warnings.None)] // safe design is assumed
        [DataRow("DerivedXmlDocument", "LoadXml(text)",   Warnings.None)] // safe design is assumed
        [DataRow("XmlDocument",        "InnerXml = text", Warnings.OnFramework)]
        [DataTestMethod]
        public async Task XmlDocumentDefaultsClassMember(string className, string sink, Warnings expectWarnings)
        {
            var cSharpTest3 = $@"
#pragma warning disable 8019
    using System.Xml;
    using System.Configuration;
#pragma warning restore 8019

class DerivedXmlDocument : XmlDocument {{}}

class Xxe
{{
#pragma warning disable 618
    private static {className} Parser = new {className}();
#pragma warning restore 618

    public static void parseUpload(string text)
    {{
        Parser.{sink};
    }}
}}";

            var visualBasicTest3 = !sink.Contains("=") ? $@"
#Disable Warning BC50001
    Imports System.Xml
    Imports System.Configuration
#Enable Warning BC50001

Class DerivedXmlDocument
    Inherits XmlDocument
End Class

Class Xxe
#Disable Warning BC40000
    Private Shared Parser As {className} = New {className}()
#Enable Warning BC40000

    Public Shared Sub parseUpload(text As String)
        Parser.{sink}
    End Sub
End Class
" : null; // todo: how to set property of a temporary object in VB?

            await Verify(cSharpTest3, visualBasicTest3, Expected, expectWarnings).ConfigureAwait(false);
        }

        [TestCategory("Detect")]
        [DataRow("XmlDocument",         "Load(text)",      Warnings.OnFramework)]
        [DataRow("XmlDocument",         "LoadXml(text)",   Warnings.OnFramework)]
        [DataRow("XmlDocument",         "InnerXml = text", Warnings.OnFramework)]
        // Special case: Load is overridden and resolver is set to null in all versions
        [DataRow("ConfigXmlDocument",   "Load(text)",      Warnings.None)]
        [DataRow("ConfigXmlDocument",   "LoadXml(text)",   Warnings.OnFramework)]
        [DataRow("XmlDataDocument",     "Load(text)",      Warnings.OnFramework)]
        // Special case XmlDataDocument.LoadXml throws NotSupportedException
        [DataRow("XmlDataDocument",     "LoadXml(text)",   Warnings.None)]
        [DataRow("DerivedXmlDocument",  "Load(text)",      Warnings.None)] // safe design is assumed
        [DataRow("DerivedXmlDocument",  "LoadXml(text)",   Warnings.None)] // safe design is assumed
        [DataRow("XmlDocument",         "InnerXml = text", Warnings.OnFramework)]
        [DataRow("XmlFileInfoDocument", "Load(text)",      Warnings.OnFramework)]
        [DataRow("XmlTransformableDocument", "Load(text)", Warnings.OnFramework)]
        [DataTestMethod]
        public async Task XmlDocumentDefaults(string className, string sink, Warnings expectWarnings)
        {
            var cSharpTest = $@"
#pragma warning disable 8019
    using System.Xml;
    using System.Configuration;
    using Microsoft.Web.XmlTransform;
#pragma warning restore 8019

class DerivedXmlDocument : XmlDocument {{}}

class Xxe
{{
    public static void parseUpload(string text)
    {{
#pragma warning disable 618
        var xmlDoc = new {className}();
        xmlDoc.{sink};
#pragma warning restore 618
    }}
}}";

            var visualBasicTest = $@"
#Disable Warning BC50001
    Imports System.Xml
    Imports System.Configuration
    Imports Microsoft.Web.XmlTransform
#Enable Warning BC50001

Class DerivedXmlDocument
    Inherits XmlDocument
End Class

Class Xxe
    Public Shared Sub parseUpload(text As String)
#Disable Warning BC40000
        Dim xmlDoc As New {className}()
        xmlDoc.{sink}
#Enable Warning BC40000
    End Sub
End Class
";

            await Verify(cSharpTest, visualBasicTest, Expected, expectWarnings).ConfigureAwait(false);

            var cSharpTest2 = $@"
#pragma warning disable 8019
    using System.Xml;
    using System.Configuration;
    using Microsoft.Web.XmlTransform;
#pragma warning restore 8019

class DerivedXmlDocument : XmlDocument {{}}

class Xxe
{{
    public static void parseUpload(string text)
    {{
#pragma warning disable 618
        new {className}().{sink};
#pragma warning restore 618
    }}
}}";

            var visualBasicTest2 = !sink.Contains("=") ? $@"
#Disable Warning BC50001
    Imports System.Xml
    Imports System.Configuration
    Imports Microsoft.Web.XmlTransform
#Enable Warning BC50001

Class DerivedXmlDocument
    Inherits XmlDocument
End Class

Class Xxe
    Public Shared Sub parseUpload(text As String)
#Disable Warning BC40000
        Call New {className}().{sink}
#Enable Warning BC40000
    End Sub
End Class
" : null; // todo: how to set property of a temporary object in VB?

            await Verify(cSharpTest2, visualBasicTest2, Expected, expectWarnings).ConfigureAwait(false);

// todo: different VB specific field declaration syntax is not recognized
//            var visualBasicTest4 = @"
//Imports System.Xml

//Class Xxe
//    Private Shared Parser As New XmlDocument()

//    Public Shared Sub parseUpload(path As String)
//        Parser.Load(path)
//    End Sub
//End Class
//";
//            await VerifyVisualBasicDiagnostic(visualBasicTest4, expected, dotNetVersion: VulnerableVersion).ConfigureAwait(false);
//            // defaults are safe starting 4.5.2
//            await VerifyVisualBasicDiagnostic(visualBasicTest4, dotNetVersion: SafeVersion).ConfigureAwait(false);
        }

        private const string SecureResolverText = "new XmlSecureResolver(new XmlUrlResolver(), \"http://myLocalSite/\")";

        [TestCategory("Detect")]
        [DataRow("XmlDocument",       "()",     "XmlResolver", "null",                 "Load(text)",      Warnings.None)]
        [DataRow("XmlDocument",       "()",     "XmlResolver", SecureResolverText,     "Load(text)",      Warnings.None)]
        [DataRow("XmlDocument",       "()",     "XmlResolver", "new XmlUrlResolver()", "Load(text)",      Warnings.Always)]
        [DataRow("XmlDocument",       "()",     "XmlResolver", "null",                 "LoadXml(text)",   Warnings.None)]
        [DataRow("XmlDocument",       "()",     "XmlResolver", SecureResolverText,     "LoadXml(text)",   Warnings.None)]
        [DataRow("XmlDocument",       "()",     "XmlResolver", "new XmlUrlResolver()", "LoadXml(text)",   Warnings.Always)]
        [DataRow("XmlDocument",       "()",     "XmlResolver", "null",                 "InnerXml = text", Warnings.None)]
        [DataRow("XmlDocument",       "()",     "XmlResolver", SecureResolverText,     "InnerXml = text", Warnings.None)]
        [DataRow("XmlDocument",       "()",     "XmlResolver", "new XmlUrlResolver()", "InnerXml = text", Warnings.Always)]
        [DataRow("ConfigXmlDocument", "()",     "XmlResolver", "null",                 "Load(text)",      Warnings.None)]
        [DataRow("ConfigXmlDocument", "()",     "XmlResolver", SecureResolverText,     "Load(text)",      Warnings.None)]
        // special case, ConfigXmlDocument.Load doesn't use the resolver
        [DataRow("ConfigXmlDocument", "()",     "XmlResolver", "new XmlUrlResolver()", "Load(text)",      Warnings.None)]
        [DataRow("ConfigXmlDocument", "()",     "XmlResolver", "null",                 "LoadXml(text)",   Warnings.None)]
        [DataRow("ConfigXmlDocument", "()",     "XmlResolver", SecureResolverText,     "LoadXml(text)",   Warnings.None)]
        [DataRow("ConfigXmlDocument", "()",     "XmlResolver", "new XmlUrlResolver()", "LoadXml(text)",   Warnings.Always)]
        [DataRow("XmlDataDocument",   "()",     "XmlResolver", "null",                 "Load(text)",      Warnings.None)]
        [DataRow("XmlDataDocument",   "()",     "XmlResolver", SecureResolverText,     "Load(text)",      Warnings.None)]
        [DataRow("XmlDataDocument",   "()",     "XmlResolver", "new XmlUrlResolver()", "Load(text)",      Warnings.Always)]
        [DataRow("XmlTextReader",     "(text)", "XmlResolver", "null",                 "Read()",          Warnings.None)]
        [DataRow("XmlTextReader",     "(text)", "XmlResolver", SecureResolverText,     "Read()",          Warnings.None)]
        [DataRow("XmlTextReader",     "(text)", "XmlResolver", "new XmlUrlResolver()", "Read()",          Warnings.Always)]
        [DataRow("XmlTransformableDocument", "()", "XmlResolver", "null",                 "Load(text)", Warnings.None)]
        [DataRow("XmlTransformableDocument", "()", "XmlResolver", SecureResolverText,     "Load(text)", Warnings.None)]
        [DataRow("XmlTransformableDocument", "()", "XmlResolver", "new XmlUrlResolver()", "Load(text)", Warnings.Always)]
        [DataRow("XmlFileInfoDocument",      "()", "XmlResolver", "null",                 "Load(text)", Warnings.None)]
        [DataRow("XmlFileInfoDocument",      "()", "XmlResolver", SecureResolverText,     "Load(text)", Warnings.None)]
        [DataRow("XmlFileInfoDocument",      "()", "XmlResolver", "new XmlUrlResolver()", "Load(text)", Warnings.Always)]
        [DataTestMethod]
        public async Task XmlDocumentBased(string className, string constructorParams, string property, string value, string sink, Warnings expectWarnings)
        {
            var vbValue = value.Replace("null", "Nothing");
            vbValue = Regex.Replace(vbValue, @"^\(([^\)]*)\)(.*)", "DirectCast($2, $1)", RegexOptions.Multiline);

            var cSharpTest = $@"
#pragma warning disable 8019
    using System.Xml;
    using System.Configuration;
    using Microsoft.Web.XmlTransform;
#pragma warning restore 8019

class Xxe
{{
    public static void parseUpload(string text)
    {{
#pragma warning disable 618
        var xmlDoc = new {className}{constructorParams};
        xmlDoc.{property} = {value};
        xmlDoc.{sink};
#pragma warning restore 618
    }}
}}";

            var visualBasicTest = $@"
#Disable Warning BC50001
    Imports System.Xml
    Imports System.Configuration
    Imports Microsoft.Web.XmlTransform
#Enable Warning BC50001

Class Xxe
    Public Shared Sub parseUpload(text As String)
#Disable Warning BC40000
        Dim xmlDoc As New {className}{constructorParams}
        xmlDoc.{property} = {vbValue}
        xmlDoc.{sink}
#Enable Warning BC40000
    End Sub
End Class
";

            await Verify(cSharpTest, visualBasicTest, Expected, expectWarnings).ConfigureAwait(false);

            var cSharpTest2 = $@"
#pragma warning disable 8019
    using System.Xml;
    using System.Configuration;
    using Microsoft.Web.XmlTransform;
#pragma warning restore 8019

class Xxe
{{
    public static void parseUpload(string text)
    {{
#pragma warning disable 618
        var xmlDoc = new {className}{constructorParams} {{{property} = {value}}};
        xmlDoc.{sink};
#pragma warning restore 618
    }}
}}";

            var visualBasicTest2 = $@"
#Disable Warning BC50001
    Imports System.Xml
    Imports System.Configuration
    Imports Microsoft.Web.XmlTransform
#Enable Warning BC50001

Class Xxe
    Public Shared Sub parseUpload(text As String)
#Disable Warning BC40000
        Dim xmlDoc As New {className}{constructorParams} With {{.{property} = {vbValue}}}
        xmlDoc.{sink}
#Enable Warning BC40000
    End Sub
End Class
";
            await Verify(cSharpTest2, visualBasicTest2, Expected, expectWarnings).ConfigureAwait(false);

            var cSharpTest3 = $@"
#pragma warning disable 8019
    using System.Xml;
    using System.Configuration;
    using Microsoft.Web.XmlTransform;
#pragma warning restore 8019

class Xxe
{{
    public static void parseUpload(string text)
    {{
#pragma warning disable 618
        new {className}{constructorParams} {{{property} = {value}}}.{sink};
#pragma warning restore 618
    }}
}}";

            var visualBasicTest3 = !sink.Contains("=") ? $@"
#Disable Warning BC50001
    Imports System.Xml
    Imports System.Configuration
    Imports Microsoft.Web.XmlTransform
#Enable Warning BC50001

Class Xxe
    Public Shared Sub parseUpload(text As String)
#Disable Warning BC40000
        Call New {className}{constructorParams} With {{.{property} = {vbValue}}}.{sink}
#Enable Warning BC40000
    End Sub
End Class
"
                                       : null; // todo: how to set property of a temporary object in VB?
            await Verify(cSharpTest3, visualBasicTest3, Expected, expectWarnings).ConfigureAwait(false);

            var cSharpTest4 = $@"
#pragma warning disable 8019
    using System.Xml;
    using System.Configuration;
    using Microsoft.Web.XmlTransform;
#pragma warning restore 8019

class Xxe
{{
#pragma warning disable 618
    public static void parseUpload(string text, {className} xmlDoc)
    {{
        xmlDoc.{property} = {value};
        xmlDoc.{sink};
#pragma warning restore 618
    }}
}}";

            var visualBasicTest4 = $@"
#Disable Warning BC50001
    Imports System.Xml
    Imports System.Configuration
    Imports Microsoft.Web.XmlTransform
#Enable Warning BC50001

Class Xxe
#Disable Warning BC40000
    Public Shared Sub parseUpload(text As String, xmlDoc As {className})
        xmlDoc.{property} = {vbValue}
        xmlDoc.{sink}
#Enable Warning BC40000
    End Sub
End Class
";
            await Verify(cSharpTest4, visualBasicTest4, Expected, expectWarnings).ConfigureAwait(false);
        }

        [TestCategory("Detect")]
        [TestMethod]
        public async Task XmlTextReaderFile()
        {
            const string cSharpTest = @"
using System.Xml;

class Xxe
{
    public static void parseUpload(string path)
    {
        var reader = new XmlTextReader(path);
        reader.Read();
    }
}";

            const string visualBasicTest = @"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim reader = new XmlTextReader(path)
        reader.Read()
    End Sub
End Class
";
            await VerifyDiagnosticSafeUnsafe(cSharpTest, visualBasicTest, Expected).ConfigureAwait(false);

            const string cSharpTest2 = @"
using System.Xml;

class Xxe
{
    public static void parseUpload(string path)
    {
        new XmlTextReader(path).Read();
    }
}";

            const string visualBasicTest2 = @"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Call new XmlTextReader(path).Read()
    End Sub
End Class
";
            await VerifyDiagnosticSafeUnsafe(cSharpTest2, visualBasicTest2, Expected).ConfigureAwait(false);
        }

        [TestCategory("Ignore")]
        [TestMethod]
        public async Task XmlTextReaderDerived()
        {
            const string cSharpTest = @"
using System.Xml;

class DerivedXmlTextReader : XmlTextReader
{
    public DerivedXmlTextReader(string path) : base(path) {}
}

class Xxe
{
    public static void parseUpload(string path)
    {
        var reader = new DerivedXmlTextReader(path);
        reader.Read();
    }
}";

            const string visualBasicTest = @"
Imports System.Xml

Class DerivedXmlTextReader
    Inherits XmlTextReader
    Public Sub New(path As String)
        MyBase.New(path)
    End Sub
End Class

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim reader = new DerivedXmlTextReader(path)
        reader.Read()
    End Sub
End Class
";
            // safe design is assumed
            await VerifyNoWarnings(cSharpTest, visualBasicTest).ConfigureAwait(false);
        }

        [TestCategory("Detect")]
        [TestMethod]
        public async Task XmlTextReaderString()
        {
            const string cSharpTest = @"
using System.Xml;
using System.IO;

class Xxe
{
    public static void parseUpload(string xmlContent)
    {
        var reader = new XmlTextReader(new StringReader(xmlContent));
        reader.Read();
    }
}";

            const string visualBasicTest = @"
Imports System.Xml
Imports System.IO

Class Xxe
    Public Shared Sub parseUpload(xmlContent As String)
        Dim reader = new XmlTextReader(new StringReader(xmlContent))
        reader.Read()
    End Sub
End Class
";
            await VerifyDiagnosticSafeUnsafe(cSharpTest, visualBasicTest, Expected).ConfigureAwait(false);

            const string cSharpTest2 = @"
using System.Xml;
using System.IO;

class Xxe
{
    public static void parseUpload(string xmlContent)
    {
        new XmlTextReader(new StringReader(xmlContent)).Read();
    }
}";

            const string visualBasicTest2 = @"
Imports System.Xml
Imports System.IO

Class Xxe
    Public Shared Sub parseUpload(xmlContent As String)
        Call new XmlTextReader(new StringReader(xmlContent)).Read()
    End Sub
End Class
";
            await VerifyDiagnosticSafeUnsafe(cSharpTest2, visualBasicTest2, Expected).ConfigureAwait(false);
        }

        [TestCategory("Ignore")]
        [DataRow("XmlResolver",   "null")]
        [DataRow("XmlResolver",   SecureResolverText)]
        [DataRow("ProhibitDtd",   "true")]
        [DataRow("DtdProcessing", "DtdProcessing.Prohibit")]
        [DataRow("DtdProcessing", "0")]
        [DataRow("DtdProcessing", "DtdProcessing.Ignore")]
        [DataRow("DtdProcessing", "(DtdProcessing)1")]
        [DataTestMethod]
        public async Task XmlTextReaderStringSafeSettings(string property, string value)
        {
            var vbValue = value.Replace("null", "Nothing");
            vbValue = Regex.Replace(vbValue, @"^\(([^\)]*)\)(.*)", "DirectCast($2, $1)", RegexOptions.Multiline);

            var cSharpTest = $@"
using System.Xml;
using System.IO;

class Xxe
{{
    public static void parseUpload(string xmlContent)
    {{
        var xmlDoc = new XmlTextReader(new StringReader(xmlContent));
#pragma warning disable 618
        xmlDoc.{property} = {value};
#pragma warning restore 618
        xmlDoc.Read();
    }}
}}";

            var visualBasicTest = $@"
Imports System.Xml
Imports System.IO

Class Xxe
    Public Shared Sub parseUpload(xmlContent As String)
        Dim reader = new XmlTextReader(new StringReader(xmlContent))
#Disable Warning BC40000
        reader.{property} = {vbValue}
#Enable Warning BC40000
        reader.Read()
    End Sub
End Class
";
            await VerifyNoWarnings(cSharpTest, visualBasicTest).ConfigureAwait(false);

            var cSharpTest2 = $@"
using System.Xml;
using System.IO;

class Xxe
{{
    public static void parseUpload(string xmlContent)
    {{
#pragma warning disable 618
        var xmlDoc = new XmlTextReader(new StringReader(xmlContent)) {{{property} = {value}}};
#pragma warning restore 618
        xmlDoc.Read();
    }}
}}";

            var visualBasicTest2 = $@"
Imports System.Xml
Imports System.IO

Class Xxe
    Public Shared Sub parseUpload(xmlContent As String)
#Disable Warning BC40000
        Dim reader = new XmlTextReader(new StringReader(xmlContent)) With {{.{property} = {vbValue}}}
#Enable Warning BC40000
        reader.Read()
    End Sub
End Class
";
            await VerifyNoWarnings(cSharpTest2, visualBasicTest2).ConfigureAwait(false);

            var cSharpTest3 = $@"
using System.Xml;
using System.IO;

class Xxe
{{
    public static void parseUpload(string xmlContent)
    {{
#pragma warning disable 618
        new XmlTextReader(new StringReader(xmlContent)) {{{property} = {value}}}.Read();
#pragma warning restore 618
    }}
}}";

            var visualBasicTest3 = $@"
Imports System.Xml
Imports System.IO

Class Xxe
    Public Shared Sub parseUpload(xmlContent As String)
#Disable Warning BC40000
        Call new XmlTextReader(new StringReader(xmlContent)) With {{.{property} = {vbValue}}}.Read()
#Enable Warning BC40000
    End Sub
End Class
";
            await VerifyNoWarnings(cSharpTest3, visualBasicTest3).ConfigureAwait(false);
        }

        [TestCategory("Ignore")]
        [TestMethod]
        public async Task XmlReaderCreateDefaultXmlReaderSettings()
        {
            const string cSharpTest = @"
using System.Xml;

class Xxe
{
    public static void parseUpload(string path)
    {
        var settings = new XmlReaderSettings();
        var reader = XmlReader.Create(path, settings);
    }
}";

            const string visualBasicTest = @"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim settings = new XmlReaderSettings()
        Dim reader = XmlReader.Create(path, settings)
    End Sub
End Class
";
            await VerifyNoWarnings(cSharpTest, visualBasicTest).ConfigureAwait(false);

            const string cSharpTest2 = @"
using System.Xml;

class Xxe
{
    public static void parseUpload(string path)
    {
        var reader = XmlReader.Create(path);
    }
}";

            const string visualBasicTest2 = @"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim reader = XmlReader.Create(path)
    End Sub
End Class
";

            await VerifyNoWarnings(cSharpTest2, visualBasicTest2).ConfigureAwait(false);

            const string cSharpTest3 = @"
using System.Xml;
using System.IO;

class Xxe
{
    public static void parseUpload(string path)
    {
        var reader = XmlReader.Create(new StringReader(path));
        XmlReader.Create(new StringReader(path));
    }
}";

            const string visualBasicTest3 = @"
Imports System.Xml
Imports System.IO

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim reader = XmlReader.Create(new StringReader(path))
        XmlReader.Create(new StringReader(path))
    End Sub
End Class
";

            await VerifyNoWarnings(cSharpTest3, visualBasicTest3).ConfigureAwait(false);

            const string cSharpTest4 = @"
using System.Xml;

class Xxe
{
    public static void parseUpload(string path)
    {
        var reader = XmlTextReader.Create(path); // access to a static member of a type via a derived type
    }
}";

            const string visualBasicTest4 = @"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim reader = XmlTextReader.Create(path)
    End Sub
End Class
";

            await VerifyNoWarnings(cSharpTest4, visualBasicTest4).ConfigureAwait(false);
        }

        [TestCategory("Ignore")]
        [DataRow("XmlResolver",   "null")]
        [DataRow("XmlResolver",   SecureResolverText)]
        [DataRow("ProhibitDtd",   "true")]
        [DataRow("DtdProcessing", "DtdProcessing.Prohibit")]
        [DataRow("DtdProcessing", "0")]
        [DataRow("DtdProcessing", "DtdProcessing.Ignore")]
        [DataRow("DtdProcessing", "(DtdProcessing)1")]
        [DataTestMethod]
        public async Task XmlReaderCreateDtdProcessingDisabled(string property, string value)
        {
            var vbValue = value.Replace("null", "Nothing");
            vbValue = Regex.Replace(vbValue, @"^\(([^\)]*)\)(.*)", "DirectCast($2, $1)", RegexOptions.Multiline);

            var cSharpTest = $@"
using System.Xml;

class Xxe
{{
    public static void parseUpload(string path)
    {{
        XmlReaderSettings settings = new XmlReaderSettings();
#pragma warning disable 618
        settings.{property} = {value};
#pragma warning restore 618
        XmlReader reader = XmlReader.Create(path, settings);
    }}
}}";

            var visualBasicTest = $@"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim settings = new XmlReaderSettings()
#Disable Warning BC40000
        settings.{property} = {vbValue}
#Enable Warning BC40000
        Dim reader = XmlReader.Create(path, settings)
    End Sub
End Class
";
            await VerifyNoWarnings(cSharpTest, visualBasicTest).ConfigureAwait(false);

            var cSharpTest2 = $@"
using System.Xml;

class Xxe
{{
    public static void parseUpload(string path)
    {{
#pragma warning disable 618
        XmlReaderSettings settings = new XmlReaderSettings {{{property} = {value}}};
#pragma warning restore 618
        XmlReader reader = XmlReader.Create(path, settings);
    }}
}}";

            var visualBasicTest2 = $@"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
#Disable Warning BC40000
        Dim settings = new XmlReaderSettings() With {{.{property} = {vbValue}}}
#Enable Warning BC40000
        Dim reader = XmlReader.Create(path, settings)
    End Sub
End Class
";
            await VerifyNoWarnings(cSharpTest2, visualBasicTest2).ConfigureAwait(false);

            var cSharpTest3 = $@"
using System.Xml;

class Xxe
{{
    public static void parseUpload(string path)
    {{
#pragma warning disable 618
        XmlReader.Create(path, new XmlReaderSettings {{{property} = {value}}});
#pragma warning restore 618
    }}
}}";

            var visualBasicTest3 = $@"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
#Disable Warning BC40000
        XmlReader.Create(path, new XmlReaderSettings() With {{.{property} = {vbValue}}})
#Enable Warning BC40000
    End Sub
End Class
";
            await VerifyNoWarnings(cSharpTest3, visualBasicTest3).ConfigureAwait(false);
        }

        [TestCategory("Detect")]
        [DataRow("ProhibitDtd   = false")]
        [DataRow("DtdProcessing = DtdProcessing.Parse")]
        [DataRow("DtdProcessing = (DtdProcessing)2")]
        [DataTestMethod]
        public async Task XmlReaderCreateDtdProcessingEnabled(string dtdProcessing)
        {
            var vbDtdProcessing = Regex.Replace(dtdProcessing, "\\(([^\\)]*)\\)(.*)", "DirectCast($2, $1)");

            var cSharpTest = $@"
using System.Xml;

class Xxe
{{
    public static void parseUpload(string path)
    {{
        XmlReaderSettings settings = new XmlReaderSettings();
#pragma warning disable 618
        settings.{dtdProcessing};
#pragma warning restore 618
        XmlReader reader = XmlReader.Create(path, settings);
    }}
}}";

            var visualBasicTest = $@"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
        Dim settings = new XmlReaderSettings()
#Disable Warning BC40000
        settings.{vbDtdProcessing}
#Enable Warning BC40000
        Dim reader = XmlReader.Create(path, settings)
    End Sub
End Class
";
            await VerifyDiagnosticSafeUnsafe(cSharpTest, visualBasicTest, Expected).ConfigureAwait(false);

            var cSharpTest2 = $@"
using System.Xml;

public class TestClass
{{
    public void TestMethod(string path)
    {{
#pragma warning disable 618
        XmlReaderSettings settings = new XmlReaderSettings(){{ {dtdProcessing} }};
#pragma warning restore 618
        XmlReader reader = XmlReader.Create(path, settings);
    }}
}}";

            var visualBasicTest2 = $@"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
#Disable Warning BC40000
        Dim settings = new XmlReaderSettings() With {{.{vbDtdProcessing}}}
#Enable Warning BC40000
        Dim reader = XmlReader.Create(path, settings)
    End Sub
End Class
";
            await VerifyDiagnosticSafeUnsafe(cSharpTest2, visualBasicTest2, Expected).ConfigureAwait(false);

            var cSharpTest3 = $@"
using System.Xml;

public class TestClass
{{
    public void TestMethod(string path)
    {{
#pragma warning disable 618
        XmlReader.Create(path, new XmlReaderSettings(){{ {dtdProcessing} }});
#pragma warning restore 618
    }}
}}";

            var visualBasicTest3 = $@"
Imports System.Xml

Class Xxe
    Public Shared Sub parseUpload(path As String)
#Disable Warning BC40000
        XmlReader.Create(path, new XmlReaderSettings() With {{.{vbDtdProcessing}}})
#Enable Warning BC40000
    End Sub
End Class
";
            await VerifyDiagnosticSafeUnsafe(cSharpTest3, visualBasicTest3, Expected).ConfigureAwait(false);
        }
    }
}
