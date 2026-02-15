using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EntityMappingAnalyzer.Models;

namespace EntityMappingAnalyzer.Services;

/// <summary>
/// Rewrites C# code using Roslyn to replace old entities with new ones
/// </summary>
public class CodeRewriterService
{
    /// <summary>
    /// Rewrites a document to replace old entity references with new entity references
    /// </summary>
    public async Task<Document> RewriteDocumentAsync(
        Document document,
        EntityMapping mapping,
        CancellationToken cancellationToken = default)
    {
        if (mapping.OldEntity == null || mapping.NewEntity == null)
        {
            throw new ArgumentException("EntityMapping must have both OldEntity and NewEntity", nameof(mapping));
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return document;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return document;
        }

        var rewriter = new EntityRewriter(semanticModel, mapping);
        var newRoot = rewriter.Visit(root);

        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    /// Custom syntax rewriter that replaces old entity references with new entity references
    /// </summary>
    private class EntityRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _semanticModel;
        private readonly EntityMapping _mapping;
        private readonly Dictionary<string, string> _propertyMappings;

        public EntityRewriter(SemanticModel semanticModel, EntityMapping mapping)
        {
            _semanticModel = semanticModel;
            _mapping = mapping;
            
            // Build property name mapping dictionary for quick lookup
            _propertyMappings = new Dictionary<string, string>();
            foreach (var propMapping in mapping.PropertyMappings)
            {
                if (propMapping.IsMatched && 
                    propMapping.Action != MappingAction.Ignored &&
                    !string.IsNullOrEmpty(propMapping.OldPropertyName) && 
                    !string.IsNullOrEmpty(propMapping.NewPropertyName))
                {
                    _propertyMappings[propMapping.OldPropertyName] = propMapping.NewPropertyName;
                }
            }
        }

        /// <summary>
        /// Replace simple identifier names (e.g., UserProfile)
        /// </summary>
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (ShouldReplaceIdentifier(node, out var newName))
            {
                return SyntaxFactory.IdentifierName(newName)
                    .WithTriviaFrom(node);
            }

            return base.VisitIdentifierName(node);
        }

        /// <summary>
        /// Replace qualified names (e.g., OldNamespace.UserProfile)
        /// </summary>
        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(node);
            var symbol = symbolInfo.Symbol;

            if (IsOldEntitySymbol(symbol))
            {
                // Replace the entire qualified name with the new entity's fully qualified name
                var newQualifiedName = CreateQualifiedName(_mapping.NewEntity!.Namespace, _mapping.NewEntity.ClassName);
                return newQualifiedName.WithTriviaFrom(node);
            }

            return base.VisitQualifiedName(node);
        }

        /// <summary>
        /// Replace generic names (e.g., List<UserProfile>)
        /// </summary>
        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            // Visit type arguments first to replace entity references inside generics
            var newNode = (GenericNameSyntax)base.VisitGenericName(node)!;

            // Check if the generic type itself needs to be replaced
            if (ShouldReplaceIdentifier(node, out var newName))
            {
                return SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier(newName),
                    newNode.TypeArgumentList)
                    .WithTriviaFrom(node);
            }

            return newNode;
        }

        /// <summary>
        /// Update using directives to reference new namespace
        /// </summary>
        public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
        {
            if (node.Name == null)
            {
                return base.VisitUsingDirective(node);
            }

            var nameText = node.Name.ToString();
            
            // Check if this using directive references the old entity's namespace
            if (nameText == _mapping.OldEntity!.Namespace)
            {
                var newNamespace = CreateQualifiedName(_mapping.NewEntity!.Namespace);
                return node.WithName(newNamespace).WithTriviaFrom(node);
            }

            // Also check if it's a parent namespace
            if (_mapping.OldEntity.Namespace.StartsWith(nameText + "."))
            {
                var newNamespace = _mapping.NewEntity!.Namespace.Split('.')
                    .Take(_mapping.OldEntity.Namespace.Split('.').Length)
                    .Aggregate((a, b) => a + "." + b);
                
                return node.WithName(CreateQualifiedName(newNamespace)).WithTriviaFrom(node);
            }

            return base.VisitUsingDirective(node);
        }

        /// <summary>
        /// Replace member access expressions to handle property renames
        /// </summary>
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var newNode = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

            // Check if the member being accessed is a property that needs to be renamed
            var symbolInfo = _semanticModel.GetSymbolInfo(node);
            var symbol = symbolInfo.Symbol;

            if (symbol is IPropertySymbol propertySymbol)
            {
                // Check if this property belongs to the old entity
                if (IsOldEntitySymbol(propertySymbol.ContainingType))
                {
                    var oldPropertyName = propertySymbol.Name;
                    if (_propertyMappings.TryGetValue(oldPropertyName, out var newPropertyName) && 
                        oldPropertyName != newPropertyName)
                    {
                        // Replace the property name
                        var newIdentifier = SyntaxFactory.IdentifierName(newPropertyName)
                            .WithTriviaFrom(newNode.Name);
                        
                        return newNode.WithName((SimpleNameSyntax)newIdentifier);
                    }
                }
            }

            return newNode;
        }

        /// <summary>
        /// Replace property declarations (for entity class definitions)
        /// </summary>
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var newNode = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;

            // Check if we're inside the old entity class definition
            var propertySymbol = _semanticModel.GetDeclaredSymbol(node);
            if (propertySymbol != null && IsOldEntitySymbol(propertySymbol.ContainingType))
            {
                var oldPropertyName = propertySymbol.Name;
                if (_propertyMappings.TryGetValue(oldPropertyName, out var newPropertyName) && 
                    oldPropertyName != newPropertyName)
                {
                    // Rename the property
                    return newNode.WithIdentifier(
                        SyntaxFactory.Identifier(newPropertyName)
                            .WithTriviaFrom(node.Identifier));
                }
            }

            return newNode;
        }

        /// <summary>
        /// Replace class declarations (for the entity class itself)
        /// </summary>
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var classSymbol = _semanticModel.GetDeclaredSymbol(node);
            
            if (IsOldEntitySymbol(classSymbol))
            {
                // This is the old entity class definition - replace the class name
                var newNode = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
                
                return newNode.WithIdentifier(
                    SyntaxFactory.Identifier(_mapping.NewEntity!.ClassName)
                        .WithTriviaFrom(node.Identifier));
            }

            return base.VisitClassDeclaration(node);
        }

        /// <summary>
        /// Replace namespace declarations
        /// </summary>
        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var namespaceSymbol = _semanticModel.GetDeclaredSymbol(node);
            
            // If this namespace contains the old entity, update it to the new namespace
            if (namespaceSymbol != null && namespaceSymbol.ToDisplayString() == _mapping.OldEntity!.Namespace)
            {
                var newNode = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;
                var newName = CreateQualifiedName(_mapping.NewEntity!.Namespace);
                
                return newNode.WithName(newName).WithTriviaFrom(node);
            }

            return base.VisitNamespaceDeclaration(node);
        }

        /// <summary>
        /// Replace file-scoped namespace declarations (C# 10+)
        /// </summary>
        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var namespaceSymbol = _semanticModel.GetDeclaredSymbol(node);
            
            // If this namespace contains the old entity, update it to the new namespace
            if (namespaceSymbol != null && namespaceSymbol.ToDisplayString() == _mapping.OldEntity!.Namespace)
            {
                var newNode = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
                var newName = CreateQualifiedName(_mapping.NewEntity!.Namespace);
                
                return newNode.WithName(newName).WithTriviaFrom(node);
            }

            return base.VisitFileScopedNamespaceDeclaration(node);
        }

        /// <summary>
        /// Check if an identifier should be replaced with the new entity name
        /// </summary>
        private bool ShouldReplaceIdentifier(SimpleNameSyntax node, out string newName)
        {
            newName = string.Empty;

            var symbolInfo = _semanticModel.GetSymbolInfo(node);
            var symbol = symbolInfo.Symbol;

            if (IsOldEntitySymbol(symbol))
            {
                newName = _mapping.NewEntity!.ClassName;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check if a symbol represents the old entity
        /// </summary>
        private bool IsOldEntitySymbol(ISymbol? symbol)
        {
            if (symbol is not INamedTypeSymbol typeSymbol)
            {
                return false;
            }

            return typeSymbol.Name == _mapping.OldEntity!.ClassName &&
                   typeSymbol.ContainingNamespace?.ToDisplayString() == _mapping.OldEntity.Namespace;
        }

        /// <summary>
        /// Create a qualified name from a namespace string
        /// </summary>
        private NameSyntax CreateQualifiedName(string namespaceString, string? className = null)
        {
            var parts = namespaceString.Split('.');
            NameSyntax result = SyntaxFactory.IdentifierName(parts[0]);

            for (int i = 1; i < parts.Length; i++)
            {
                result = SyntaxFactory.QualifiedName(result, SyntaxFactory.IdentifierName(parts[i]));
            }

            if (!string.IsNullOrEmpty(className))
            {
                result = SyntaxFactory.QualifiedName(result, SyntaxFactory.IdentifierName(className));
            }

            return result;
        }
    }
}
