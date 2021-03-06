﻿using System;
using System.Linq;
using System.Collections.Generic;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using Kusto.Language.Editor;

namespace Kusto.Language.Parsing
{
    using static Parsers<LexicalToken>;
    using static SyntaxParsers;
    using Utils;

    /// <summary>
    /// Parsers for the Kusto query grammar.
    /// </summary>
    public class QueryGrammar
    {
        private QueryGrammar()
        {
            this.Initialize();
        }

        private static QueryGrammar s_Instance;

        /// <summary>
        /// Gets the <see cref="QueryGrammar"/> associated with the specified <see cref="GlobalState"/>.
        /// </summary>
        public static QueryGrammar From(GlobalState globals)
        {
#if false // TODO: when grammar is made dependent on globals
            if (!globals.Cache.TryGetValue<QueryGrammar2>(out var grammar))
            {
                grammar = globals.Cache.GetOrCreate(() => new QueryGrammar2(globals));
            }

            return grammar;
#else
            if (s_Instance == null)
            {
                Interlocked.CompareExchange(ref s_Instance, new QueryGrammar(), null);
            }

            return s_Instance;
#endif
        }

        public Parser<LexicalToken, QueryBlock> QueryBlock { get; private set; }
        public Parser<LexicalToken, Statement> Statement { get; private set; }
        public Parser<LexicalToken, SyntaxList<SeparatedElement<Statement>>> StatementList { get; private set; }
        public Parser<LexicalToken, FunctionBody> FunctionBody { get; private set; }
        public Parser<LexicalToken, FunctionParameters> FunctionParameters { get; private set; }
        public Parser<LexicalToken, Expression> Expression { get; private set; }
        public Parser<LexicalToken, QueryOperator> FollowingPipeElementExpression { get; private set; }
        public Parser<LexicalToken, NameDeclaration> SimpleNameDeclaration { get; private set; }
        public Parser<LexicalToken, Expression> SimpleNameDeclarationExpression { get; private set; }
        public Parser<LexicalToken, NameDeclaration> BrackettedNameDeclaration { get; private set; }
        public Parser<LexicalToken, Name> IdentifierName { get; private set; }
        public Parser<LexicalToken, Name> BrackettedName { get; private set; }
        public Parser<LexicalToken, Name> BracedName { get; private set; }
        public Parser<LexicalToken, TypeExpression> ParamTypeExtended { get; private set; }
        public Parser<LexicalToken, Expression> SimpleNameReference { get; private set; }
        public Parser<LexicalToken, Expression> ConstantExpression { get; private set; }
        public Parser<LexicalToken, Expression> Literal { get; private set; }
        public Parser<LexicalToken, Expression> StringLiteral { get; private set; }
        public Parser<LexicalToken, SkippedTokens> SkippedTokens { get; private set; }

        /// <summary>
        /// Constructs the grammar as a Parser
        /// </summary>
        private void Initialize()
        {
            #region Forwards
            Parser<LexicalToken, Expression> ExpressionCore = null;
            Parser<LexicalToken, Expression> UnnamedExpressionCore = null;
            Parser<LexicalToken, NameAndTypeDeclaration> NameAndTypeDeclarationCore = null;
            Parser<LexicalToken, Expression> LiteralCore = null;
            Parser<LexicalToken, Expression> StringOrCompoundStringLiteralCore = null;
            Parser<LexicalToken, Expression> JsonValueCore = null;
            Parser<LexicalToken, Expression> PrimaryExpressionCore = null;
            Parser<LexicalToken, Expression> FunctionCallOrPathCore = null;
            Parser<LexicalToken, QueryOperator> ForkPipeOperatorCore = null;
            Parser<LexicalToken, Expression> ForkPipeExpressionCore = null;
            Parser<LexicalToken, QueryOperator> PartitionPipeOperatorCore = null;
            Parser<LexicalToken, Expression> PartitionPipeExpressionCore = null;
            Parser<LexicalToken, Statement> LetStatementCore = null;
            Parser<LexicalToken, Statement> QueryParametersStatementCore = null;

            this.Expression =
                Forward(() => ExpressionCore)
                .WithTag("<expression>");

            var UnnamedExpression =
                Forward(() => UnnamedExpressionCore)
                .WithTag("<expression>");

            var NameAndTypeDeclaration =
                Forward(() => NameAndTypeDeclarationCore)
                .WithTag("<name-and-type>");

            this.Literal =
                Forward(() => LiteralCore)
                .WithTag("<literal>");

            var StringOrCompoundStringLiteral =
                Forward(() => StringOrCompoundStringLiteralCore)
                .WithTag("<string-literal>");

            var JsonValue =
                Forward(() => JsonValueCore)
                .WithTag("<json-value>");

            var PrimaryExpression =
                Forward(() => PrimaryExpressionCore)
                .WithTag("<primary-expression>");

            var FunctionCallOrPath =
                Forward(() => FunctionCallOrPathCore)
                .WithTag("<function-call-or-path>");

            var ForkPipeOperator =
                Forward(() => ForkPipeOperatorCore)
                .WithTag("<fork-pipe-operator>");

            var ForkPipeExpression =
                Forward(() => ForkPipeExpressionCore)
                .WithTag("<fork-pipe-expression>");

            var PartitionPipeOperator =
                Forward(() => PartitionPipeOperatorCore)
                .WithTag("<partition-pipe-operator>");

            var PartitionPipeExpression =
                Forward(() => PartitionPipeExpressionCore)
                .WithTag("<partition-pipe-expression>");

            var LetStatement =
                Forward(() => LetStatementCore)
                .WithTag("<let>");

            var QueryParametersStatement =
                Forward(() => QueryParametersStatementCore)
                .WithTag("<query-parameters>");
            #endregion

            #region Names
            var ScanIdentifierName =
                Token(SyntaxKind.IdentifierToken);

            var ScanKeywordAsIdentifier =
                Match(t => t.Kind.GetCategory() == SyntaxCategory.Keyword && t.Kind.CanBeIdentifier())
                .WithTag("<keyword>");

            var KeywordAsIdentifier =
                Match(t => t.Kind.GetCategory() == SyntaxCategory.Keyword && t.Kind.CanBeIdentifier(), t => SyntaxToken.From(t))
                .WithTag("<keyword>");

            this.IdentifierName =
                Rule(First(Token(SyntaxKind.IdentifierToken), KeywordAsIdentifier),
                    token => (Name)new TokenName(token));

            this.BrackettedName =
                Rule(
                    Token(SyntaxKind.OpenBracketToken),
                    StringOrCompoundStringLiteral,
                    Token(SyntaxKind.CloseBracketToken),
                    (open, name, close) => (Name)new BrackettedName(open, name, close));

            this.BracedName =
                Rule(
                    Token(SyntaxKind.OpenBraceToken),
                    First(Token(SyntaxKind.IdentifierToken), KeywordAsIdentifier),
                    Token(SyntaxKind.CloseBracketToken),
                    (open, name, close) => (Name)new BracedName(open, name, close));

            var IdentifierNameDeclaration =
                Rule(
                    Token(SyntaxKind.IdentifierToken),
                    id => (NameDeclaration)new NameDeclaration(id))
                    .WithTag("<identifer>");

            var IdentifierNameReference =
                Rule(
                    Token(SyntaxKind.IdentifierToken),
                    id => (Expression)new NameReference(id))
                    .WithTag("<identifer>");

            var ClientParameterReference =
                // only match rule if parts are adjacent (no whitespace)
                If(And(
                    Token(SyntaxKind.OpenBraceToken),
                    Match(t => t.Kind == SyntaxKind.IdentifierToken && t.TriviaWidth == 0),
                    Match(t => t.Kind == SyntaxKind.CloseBraceToken && t.TriviaWidth == 0)),
                Rule(
                    Token(SyntaxKind.OpenBraceToken),
                    Token(SyntaxKind.IdentifierToken),
                    Token(SyntaxKind.CloseBraceToken),
                    (open, name, close) => (Expression)new NameReference(new BracedName(open, name, close), SymbolMatch.None)))
                .WithTag("<client-parameter>");

            var ScanBrackettedName =
                And(Token(SyntaxKind.OpenBracketToken),
                    OneOrMore(Token(SyntaxKind.StringLiteralToken)),
                    Optional(Token(SyntaxKind.CloseBracketToken)));

            this.BrackettedNameDeclaration =
                Rule(
                    Token(SyntaxKind.OpenBracketToken),
                    Required(StringOrCompoundStringLiteral, MissingStringLiteral),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    (openBracket, name, closeBracket) =>
                        (NameDeclaration)new NameDeclaration(new BrackettedName(openBracket, name, closeBracket)));

            var BrackettedNameReference =
                Rule(
                    Token(SyntaxKind.OpenBracketToken),
                    Required(StringOrCompoundStringLiteral, MissingStringLiteral),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    (openBracket, name, closeBracket) =>
                        (Expression)new NameReference(new BrackettedName(openBracket, name, closeBracket)));

            var KeywordNameDeclaration =
                AsIdentifierNameDeclaration(KeywordAsIdentifier)
                .WithTag("<keyword>");

            var KeywordNameReference =
                AsIdentifierNameReference(KeywordAsIdentifier)
                .WithTag("<keyword>");

            var NameTokenLiteral =
                AsTokenLiteral(First(Token(SyntaxKind.IdentifierToken), KeywordAsIdentifier));

            var ScanSimpleName =
                Or(ScanIdentifierName, ScanBrackettedName, ScanKeywordAsIdentifier);

            this.SimpleNameDeclaration =
                First(
                    IdentifierNameDeclaration,
                    BrackettedNameDeclaration,
                    KeywordNameDeclaration)
                .WithTag("<name>");

            this.SimpleNameDeclarationExpression =
                Rule(SimpleNameDeclaration, nd => (Expression)nd);

            this.SimpleNameReference =
                First(
                    IdentifierNameReference,
                    BrackettedNameReference,
                    KeywordNameReference,
                    ClientParameterReference)
                .WithTag("<name>");
            #endregion

            #region Schema and Types
            var IdentifierTypeExpression =
                AsPrimitiveTypeExpression(Token(SyntaxKind.IdentifierToken));

            var ParamType =
                AsPrimitiveTypeExpression(
                    First(
                        Token(SyntaxKind.BoolKeyword),
                        Token(SyntaxKind.BooleanKeyword).Hide(),
                        Token(SyntaxKind.DateKeyword).Hide(),
                        Token(SyntaxKind.DateTimeKeyword),
                        Token(SyntaxKind.DecimalKeyword),
                        Token(SyntaxKind.DoubleKeyword).Hide(),
                        Token(SyntaxKind.DynamicKeyword),
                        Token(SyntaxKind.GuidKeyword),
                        Token(SyntaxKind.IntKeyword),
                        Token(SyntaxKind.Int64Keyword).Hide(),
                        Token(SyntaxKind.Int8Keyword).Hide(),
                        Token(SyntaxKind.LongKeyword),
                        Token(SyntaxKind.RealKeyword),
                        Token(SyntaxKind.StringKeyword),
                        Token(SyntaxKind.TimeKeyword).Hide(),
                        Token(SyntaxKind.TimespanKeyword),
                        Token(SyntaxKind.UniqueIdKeyword).Hide()
                        ));

            this.ParamTypeExtended =
                AsPrimitiveTypeExpression(
                    First(
                        Token(SyntaxKind.BoolKeyword),
                        Token(SyntaxKind.BooleanKeyword).Hide(),
                        Token(SyntaxKind.DateKeyword).Hide(),
                        Token(SyntaxKind.DateTimeKeyword),
                        Token(SyntaxKind.DecimalKeyword),
                        Token(SyntaxKind.DoubleKeyword).Hide(),
                        Token(SyntaxKind.DynamicKeyword),
                        Token(SyntaxKind.FloatKeyword).Hide(),
                        Token(SyntaxKind.GuidKeyword),
                        Token(SyntaxKind.IntKeyword),
                        Token(SyntaxKind.Int16Keyword).Hide(),
                        Token(SyntaxKind.Int32Keyword).Hide(),
                        Token(SyntaxKind.Int64Keyword).Hide(),
                        Token(SyntaxKind.Int8Keyword).Hide(),
                        Token(SyntaxKind.LongKeyword),
                        Token(SyntaxKind.RealKeyword),
                        Token(SyntaxKind.DecimalKeyword),
                        Token(SyntaxKind.SingleKeyword).Hide(),
                        Token(SyntaxKind.StringKeyword),
                        Token(SyntaxKind.TimeKeyword).Hide(),
                        Token(SyntaxKind.TimespanKeyword),
                        Token(SyntaxKind.UIntKeyword).Hide(),
                        Token(SyntaxKind.UInt16Keyword).Hide(),
                        Token(SyntaxKind.UInt32Keyword).Hide(),
                        Token(SyntaxKind.UInt64Keyword).Hide(),
                        Token(SyntaxKind.UInt8Keyword).Hide(),
                        Token(SyntaxKind.ULongKeyword).Hide(),
                        Token(SyntaxKind.UniqueIdKeyword).Hide()
                        ));

            var ScanSchemaTypeStart =
                And(
                    ScanSimpleName,
                    Token(SyntaxKind.ColonToken),
                    Token(SyntaxKind.OpenParenToken));

            var StarExpression =
                Rule(
                    Token(SyntaxKind.AsteriskToken).Hide(),
                    (star) => (Expression)new StarExpression(star));

            var SchemaAsteriskType =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    StarExpression,
                    Token(SyntaxKind.CloseParenToken),

                    (openParen, star, closeParen) =>
                        new SchemaTypeExpression(
                            openParen,
                            new SyntaxList<SeparatedElement<Expression>>(new SeparatedElement<Expression>(star)),
                            closeParen));

            var SchemaMultipartType =
                    Rule(
                        Token(SyntaxKind.OpenParenToken),
                        SeparatedList<Expression>(Rule(NameAndTypeDeclaration, nat => (Expression)nat), SyntaxKind.CommaToken, missingElement: MissingNameAndTypeDeclarationNode, oneOrMore: true),
                        RequiredToken(SyntaxKind.CloseParenToken),

                        (openParen, columns, closeParen) =>
                            new SchemaTypeExpression(openParen, columns, closeParen));

            var SchemaType =
                First(
                    If(And(Token(SyntaxKind.OpenParenToken), Token(SyntaxKind.AsteriskToken)), SchemaAsteriskType),
                    SchemaMultipartType);

            NameAndTypeDeclarationCore =
                First(
                    Rule( // error case: missing name
                        Token(SyntaxKind.ColonToken),
                        Required(First(ParamType, IdentifierTypeExpression), MissingType),
                        (colon, type) => new NameAndTypeDeclaration((NameDeclaration)MissingNameDeclarationNode.Clone(), colon, type)),

                    If(ScanSchemaTypeStart,
                        Rule(
                            SimpleNameDeclaration,
                            Token(SyntaxKind.ColonToken),
                            SchemaType,
                            (name, colon, type) => new NameAndTypeDeclaration(name, colon, type))),

                    Rule(
                        SimpleNameDeclaration,
                        RequiredToken(SyntaxKind.ColonToken),
                        Required(First(ParamType, IdentifierTypeExpression), MissingType),
                        (name, colon, type) => new NameAndTypeDeclaration(name, colon, type)));
            #endregion

            #region Literals
            var BooleanLiteral =
                Rule(
                    Token(SyntaxKind.BooleanLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.BooleanLiteralExpression, token));

            var BooleanLiteralWithCompletion =
                First(
                    If(Or(Token("true"), Token("false")), BooleanLiteral), // completion will see ScanToken's
                    BooleanLiteral);

            var LongLiteral =
                Rule(
                    Token(SyntaxKind.LongLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.LongLiteralExpression, token));

            var RealLiteral =
                Rule(
                    Token(SyntaxKind.RealLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.RealLiteralExpression, token));

            var DecimalLiteral =
                Rule(
                    Token(SyntaxKind.DecimalLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.DecimalLiteralExpression, token));

            var IntLiteral =
                Rule(
                    Token(SyntaxKind.IntLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.IntLiteralExpression, token));

            var GuidLiteral =
                Rule(
                    Token(SyntaxKind.GuidLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.GuidLiteralExpression, token));

            var DateTimeLiteral =
                Rule(Token(SyntaxKind.DateTimeLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.DateTimeLiteralExpression, token));

            var TimespanLiteral =
                Rule(Token(SyntaxKind.TimespanLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.TimespanLiteralExpression, token));

            this.StringLiteral =
                Rule(
                    Token(SyntaxKind.StringLiteralToken),
                    token => (Expression)new LiteralExpression(SyntaxKind.StringLiteralExpression, token))
                .WithTag("<string>");

            var TypeofElement =
                First(
                    Rule(NameAndTypeDeclaration, nat => (Expression)nat),
                    StarExpression);

            var ScanTypeOfScalar =
                And(Token(SyntaxKind.TypeOfKeyword).Hide(),
                    Token(SyntaxKind.OpenParenToken),
                    Or(
                        ParamType,
                        ParamTypeExtended.Hide(),
                        And(Token(SyntaxKind.IdentifierToken).Hide(), Token(SyntaxKind.CloseParenToken))));

            var TypeofLiteral =
                First(
                    If(ScanTypeOfScalar,
                        Rule(
                            Token(SyntaxKind.TypeOfKeyword).Hide(),
                            Token(SyntaxKind.OpenParenToken),
                            First(ParamType, ParamTypeExtended.Hide(), IdentifierTypeExpression.Hide()),
                            RequiredToken(SyntaxKind.CloseParenToken),
                            (keyword, openParen, type, closeParen) =>
                                (Expression)new TypeOfLiteralExpression(
                                    keyword,
                                    openParen,
                                    new SyntaxList<SeparatedElement<Expression>>(new SeparatedElement<Expression>(type)),
                                    closeParen))),
                    Rule(
                        Token(SyntaxKind.TypeOfKeyword).Hide(),
                        RequiredToken(SyntaxKind.OpenParenToken),
                        SeparatedList(TypeofElement, SyntaxKind.CommaToken, MissingTypeNode, oneOrMore: true),
                        RequiredToken(SyntaxKind.CloseParenToken),
                        (keyword, openParen, list, closeParen) =>
                            (Expression)new TypeOfLiteralExpression(keyword, openParen, list, closeParen)
                        ));

            StringOrCompoundStringLiteralCore =
                OneOrMore(
                    Token(SyntaxKind.StringLiteralToken),
                    list => list.Count == 1
                        ? (Expression)new LiteralExpression(SyntaxKind.StringLiteralExpression, list[0])
                        : new CompoundStringLiteralExpression(new SyntaxList<SyntaxToken>(list)));

            LiteralCore =
                First(
                    StringOrCompoundStringLiteral,
                    BooleanLiteral,
                    LongLiteral,
                    RealLiteral,
                    DecimalLiteral,
                    IntLiteral,
                    GuidLiteral,
                    DateTimeLiteral,
                    TimespanLiteral,
                    TypeofLiteral,
                    ClientParameterReference);

            var KeywordTokenLiteral =
                AsTokenLiteral(KeywordAsIdentifier).WithTag("<keyword>");

            var NullLiteralExpression =
                Rule(
                    Token("null"),
                    (token) => (Expression)new LiteralExpression(SyntaxKind.NullLiteralExpression, token));

            var JsonPair =
                First(
                    Rule(Token(SyntaxKind.StringLiteralToken), RequiredToken(SyntaxKind.ColonToken), Required(JsonValue, MissingJsonValue),
                        (name, colon, value) =>
                            new JsonPair(name, colon, value)),
                    Rule(Token(SyntaxKind.ColonToken).Hide(), Required(JsonValue, MissingJsonValue),
                        (colonToken, value) =>
                            new JsonPair(CreateMissingToken(SyntaxKind.StringLiteralToken, DiagnosticFacts.GetMissingString()), colonToken, value)),
                    Rule(JsonValue.Hide(),
                        (value) =>
                            new JsonPair(CreateMissingToken(SyntaxKind.StringLiteralToken, DiagnosticFacts.GetMissingString()), CreateMissingToken(SyntaxKind.ColonToken), value)))
                .WithTag("<json-pair>");

            var JsonObject =
                Rule(
                    Token(SyntaxKind.OpenBraceToken),
                    SeparatedList(JsonPair, SyntaxKind.CommaToken, missingElement: MissingJsonPairNode),
                    RequiredToken(SyntaxKind.CloseBraceToken),
                    (openBrace, list, closeBrace) =>
                        (Expression)new JsonObjectExpression(openBrace, list, closeBrace))
                .WithTag("<json-object>");

            var JsonArray =
                Rule(
                    Token(SyntaxKind.OpenBracketToken),
                    SeparatedList(JsonValue, SyntaxKind.CommaToken, missingElement: MissingJsonValueNode),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    (openBracket, values, closeBracket) =>
                        (Expression)new JsonArrayExpression(openBracket, values, closeBracket))
                .WithTag("<json-array>");

            var DynamicLiteral =
                Rule(
                    Token(SyntaxKind.DynamicKeyword, CompletionKind.ScalarPrefix),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    Required(First(NullLiteralExpression, JsonValue), MissingJsonValue),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (dynamicKeyword, openParen, value, closeParen) =>
                        (Expression)new DynamicExpression(dynamicKeyword, openParen, value, closeParen))
                .WithTag("<dynamic>");

            var JsonNumber =
                First(
                    Rule(
                        Token(SyntaxKind.MinusToken).Hide(),
                        First(LongLiteral, RealLiteral),
                        (unaryOp, value) => (Expression)new PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, unaryOp, value)),
                    LongLiteral,
                    RealLiteral);

            JsonValueCore =
                First(
                    StringOrCompoundStringLiteral,
                    JsonNumber,
                    TimespanLiteral,
                    BooleanLiteral,
                    DateTimeLiteral,
                    GuidLiteral,
                    DecimalLiteral,
                    DynamicLiteral,
                    NullLiteralExpression,
                    JsonObject,
                    JsonArray,
                    ClientParameterReference);
            #endregion

            #region Expressions
            var ParenthesizedExpression =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    Required(Expression, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (openParen, expression, closeParen) =>
                        (Expression)new ParenthesizedExpression(openParen, expression, closeParen));

            this.ConstantExpression =
                First(
                    Literal,
                    DynamicLiteral)
                .WithTag("<constant>");

            var NumericConstantExpression =
                First(
                    LongLiteral,
                    IntLiteral,
                    RealLiteral,
                    DateTimeLiteral,
                    TimespanLiteral)
                .WithTag("<numeric-constant>");

            var ScanRenameName = Or(
                ScanIdentifierName,
                ScanKeywordAsIdentifier,
                ScanBrackettedName);

            var ScanRenameList =
                And(Token(SyntaxKind.OpenParenToken),
                    ZeroOrMore(And(ScanRenameName, Optional(Token(SyntaxKind.CommaToken)))),
                    Optional(Token(SyntaxKind.CloseParenToken)));

            var RenameName =
                First(SimpleNameDeclaration, KeywordNameDeclaration)
                .WithTag("<name>");

            var RenameList =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    SeparatedList(RenameName, SyntaxKind.CommaToken, MissingNameDeclarationNode, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (openParen, list, closeParen) =>
                        new RenameList(openParen, list, closeParen));

            var NamedExpression =
                First(
                    If(And(RenameName, Token(SyntaxKind.EqualToken)),
                        Rule(RenameName, Token(SyntaxKind.EqualToken), Required(UnnamedExpression, MissingExpression),
                            (name, equals, expr) =>
                                (Expression)new SimpleNamedExpression(name, equals, expr))),

                    If(And(ScanRenameList, Token(SyntaxKind.EqualToken)),
                        Rule(RenameList, Token(SyntaxKind.EqualToken), Required(UnnamedExpression, MissingExpression),
                            (list, equals, expr) =>
                                (Expression)new CompoundNamedExpression(list, equals, expr))),

                    UnnamedExpression)
                .WithTag("<expression>");

            var Argument =
                First(
                    If(And(Token(SyntaxKind.AsteriskToken), Or(Token(SyntaxKind.CloseParenToken), Token(SyntaxKind.CommaToken))),
                        StarExpression),
                    NamedExpression);

            var FunctionArgumentList =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    SeparatedList(Argument, SyntaxKind.CommaToken, missingElement: MissingExpressionNode),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (openParen, list, closeParen) => new ExpressionList(openParen, list, closeParen));

            var knownFunctionNames =
                Functions.All.Select(f => f.Name).Concat(
                Aggregates.All.Select(f => f.Name)).ToArray();

            // some built-in function names are not identifiers so we add the ones we know about here.
            var ScanKnownFunctionNames =
                Token(knownFunctionNames)
                .Hide();

            var KnownFunctionNameReference =
                AsIdentifierNameReference(Token(knownFunctionNames).Hide());

            var ScanFunctionCall =
                And(Or(ScanIdentifierName, ScanBrackettedName, ScanKnownFunctionNames), Token(SyntaxKind.OpenParenToken));

            var FunctionCallNames =
                First(SimpleNameReference, KnownFunctionNameReference);

            var FunctionCall =
                Rule(
                    FunctionCallNames,
                    FunctionArgumentList,
                    (name, arguments) =>
                        (Expression)new FunctionCallExpression((NameReference)name, arguments))
                .WithTag("<FunctionCall>");

            var DotCompositeFunctionCall =
                ApplyZeroOrMore(
                    FunctionCall,
                    _left =>
                        Rule(
                            _left,
                            Token(SyntaxKind.DotToken),
                            If(ScanFunctionCall, FunctionCall),
                            (left, op, right) =>
                                (Expression)new PathExpression(left, op, right)));

            var AtTokenSelector =
                Rule(Token(SyntaxKind.AtToken), token => (Expression)new AtExpression(token));

            var BarePathElementSelector =
                First(
                    AtTokenSelector,
                    IdentifierNameReference,
                    KeywordNameReference,
                    ClientParameterReference);

            // wild cards can use any keyword (but will need an asterisk somewhere)
            var ScanWildcard =
                And(
                    First(
                        And(
                            Match(t => t.Kind == SyntaxKind.IdentifierToken || t.Kind.GetCategory() == SyntaxCategory.Keyword), // can have leading trivia
                            Match(t => t.Kind == SyntaxKind.AsteriskToken && t.Trivia.Length == 0)),
                        Token(SyntaxKind.AsteriskToken)), // can have leading trivia
                    ZeroOrMore(
                        Match(t => (t.Kind == SyntaxKind.IdentifierToken
                                || t.Kind == SyntaxKind.LongLiteralToken
                                || t.Kind == SyntaxKind.AsteriskToken
                                || t.Kind.GetCategory() == SyntaxCategory.Keyword)
                                && t.Trivia.Length == 0)));

            var WildcardedNameReference =
                Convert(
                    ScanWildcard.Hide(), 
                    (IReadOnlyList<LexicalToken> list) => (Expression)
                        new NameReference(
                            new WildcardedName(
                                SyntaxToken.Identifier(list[0].Trivia, string.Concat(list.Select(t => t.Text))))))
                .WithTag("<wildcard>");

            var Conditioned =
                Rule(
                    Token(SyntaxKind.QuestionToken),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    Required(NamedExpression, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (question, openParen, expr, closeParen) =>
                        (Expression)new ConditionedExpression(question, openParen, expr, closeParen))
                .WithTag("<conditioned>");

            var InvocationExpression =
                First(
                    Rule(
                        Token(SyntaxKind.MinusToken).Hide(),
                        FunctionCallOrPath,
                        (op, expr) => (Expression)new PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, op, expr)),
                    Rule(
                        Token(SyntaxKind.PlusToken).Hide(),
                        FunctionCallOrPath,
                        (op, expr) => (Expression)new PrefixUnaryExpression(SyntaxKind.UnaryPlusExpression, op, expr)),
                    FunctionCallOrPath);

            var BrackettedPathElementSelector =
                Rule(
                    Token(SyntaxKind.OpenBracketToken),
                    Required(First(WildcardedNameReference, Conditioned, InvocationExpression).Hide(), MissingNameReference),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    (openBracket, expr, closeBracket) =>
                        (Expression)new BrackettedExpression(openBracket, expr, closeBracket));

            var PathElementSelector =
                First(
                    BarePathElementSelector,
                    BrackettedPathElementSelector);

            var BrackettedEntityNamePathElementSelector =
                Rule(
                    Token(SyntaxKind.OpenBracketToken),
                    Required(First(WildcardedNameReference, StringLiteral), MissingNameReference),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    (openBracket, expr, closeBracket) =>
                        (Expression)new BrackettedExpression(openBracket, expr, closeBracket));

            var EntityPathExpression =
                ApplyZeroOrMore(
                    PathElementSelector,
                    _left =>
                        First(
                            Rule(_left, Token(SyntaxKind.DotToken), Required(PathElementSelector, MissingNameReference),
                                (left, dot, selector) =>
                                    (Expression)new PathExpression(left, dot, selector)),
                            Rule(_left, BrackettedPathElementSelector,
                                (left, right) =>
                                    (Expression)new ElementExpression(left, right))));

            var EntityReferenceExpression =
                EntityPathExpression
                .WithTag("<entity>");

            var ScanWildcardedEntityReference =
                Or(ScanWildcard, ScanFunctionCall);

            var SimpleOrWildcardedEntityReference =
                First(
                    WildcardedNameReference,
                    SimpleNameReference)
                .WithTag("<simple-or-wildcarded-entity>");

            var WildcardedEntityReference =
                First(
                    WildcardedNameReference,
                    If(ScanFunctionCall,
                        ApplyOptional(
                            DotCompositeFunctionCall,
                            _left =>
                                Rule(
                                    _left,
                                    Token(SyntaxKind.DotToken),
                                    Required(First(WildcardedNameReference, BarePathElementSelector, BrackettedEntityNamePathElementSelector), MissingNameReference),
                                    (path, dot, selector) =>
                                        (Expression)new PathExpression(path, dot, selector)))))
                .WithTag("<wildcarded-entity>");

            var ToScalarExpression =
                Rule(
                    Token(SyntaxKind.ToScalarKeyword, CompletionKind.ScalarPrefix),
                    Optional(TokenNamedParameter("kind", KustoFacts.ToScalarKinds).Hide()),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    Required(Expression, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (name, kind, openParen, expression, closeParen) =>
                        (Expression)new ToScalarExpression(name, kind, openParen, expression, closeParen));

            var ToTableExpression =
                Rule(
                    Token(SyntaxKind.ToTableKeyword, CompletionKind.TabularPrefix).Hide(),
                    Optional(TokenNamedParameter("kind", KustoFacts.ToTableKinds).Hide()),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    Required(Expression, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (name, kind, openParen, expression, closeParen) =>
                        (Expression)new ToTableExpression(name, kind, openParen, expression, closeParen));

            FunctionCallOrPathCore =
                First(
                    ToTableExpression, // first to preempt being seen as function call

                    ApplyZeroOrMore(
                        First(
                            ToScalarExpression, // first to preempt being seen as function call
                            If(ScanFunctionCall, DotCompositeFunctionCall),
                            PrimaryExpression),

                        _left =>
                            First(
                                Rule(_left, Token(SyntaxKind.DotToken), Required(PathElementSelector, MissingNameReference),
                                    (left, dot, selector) => (Expression)new PathExpression(left, dot, selector)),
                                Rule(_left, BrackettedPathElementSelector,
                                    (left, right) => (Expression)new ElementExpression(left, right)))));

            var RequiredFunctionCallOrPath =
                Required(FunctionCallOrPath, MissingExpression);

            var UnaryPlusOrMinus =
                First(
                    Rule(
                        Token(SyntaxKind.PlusToken).Hide(),
                        RequiredFunctionCallOrPath,
                        (op, expr) => (Expression)new PrefixUnaryExpression(SyntaxKind.UnaryPlusExpression, op, expr)),
                    Rule(
                        Token(SyntaxKind.MinusToken).Hide(),
                        RequiredFunctionCallOrPath,
                        (op, expr) => (Expression)new PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, op, expr)),
                    FunctionCallOrPath);

            var RequiredUnaryPlusOrMinus =
                Required(UnaryPlusOrMinus, MissingExpression);

            var StringOperatorTokens =
                First(StringOperatorMap.Keys.Select(
                    k => IsTokenVisible(k)
                        ? SyntaxParsers.Token((SyntaxKind)k, CompletionKind.ScalarInfix, ctext: (string)(SyntaxFacts.GetText((SyntaxKind)k) + " \"|\""))
                        : SyntaxParsers.Token((SyntaxKind)k).Hide()
                        ).ToArray());

            var StringOperation =
                First(
                    Rule(
                        Token(SyntaxKind.AsteriskToken).Hide(),
                        StringOperatorTokens,
                        RequiredUnaryPlusOrMinus,
                        (left, op, right) =>
                            (Expression)new BinaryExpression(StringOperatorMap[op.Kind], new StarExpression(left), op, right)),
                    ApplyOptional(
                        UnaryPlusOrMinus,
                        _left =>
                            Rule(
                                _left,
                                StringOperatorTokens,
                                RequiredUnaryPlusOrMinus,
                                (left, op, right) =>
                                    (Expression)new BinaryExpression(StringOperatorMap[op.Kind], left, op, right))));

            var RequiredStringOperation =
                Required(StringOperation, MissingExpression);

            var Multiplicative =
                ApplyZeroOrMore(StringOperation, _left =>
                    First(
                        Rule(_left, Token(SyntaxKind.AsteriskToken, CompletionKind.ScalarInfix), RequiredStringOperation,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.MultiplyExpression, left, op, right)),

                        Rule(_left, Token(SyntaxKind.SlashToken, CompletionKind.ScalarInfix), RequiredStringOperation,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.DivideExpression, left, op, right)),

                        Rule(_left, Token(SyntaxKind.PercentToken, CompletionKind.ScalarInfix), RequiredStringOperation,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.ModuloExpression, left, op, right))
                        ));

            var RequiredMultiplicative =
                Required(Multiplicative, MissingExpression);

            var Additive =
                ApplyZeroOrMore(Multiplicative, _left =>
                    First(
                        Rule(_left, Token(SyntaxKind.PlusToken, CompletionKind.ScalarInfix), RequiredMultiplicative,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.AddExpression, left, op, right)),

                        Rule(_left, Token(SyntaxKind.MinusToken, CompletionKind.ScalarInfix), RequiredMultiplicative,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.SubtractExpression, left, op, right))
                        ));

            var RequiredAdditive =
                Required(Additive, MissingExpression);

            var Relational =
                ApplyZeroOrMore(Additive, _left =>
                    First(
                        Rule(_left, Token(SyntaxKind.LessThanToken, CompletionKind.ScalarInfix), RequiredAdditive,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.LessThanExpression, left, op, right)),

                        Rule(_left, Token(SyntaxKind.LessThanOrEqualToken, CompletionKind.ScalarInfix), RequiredAdditive,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.LessThanOrEqualExpression, left, op, right)),

                        Rule(_left, Token(SyntaxKind.GreaterThanToken, CompletionKind.ScalarInfix), RequiredAdditive,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.GreaterThanExpression, left, op, right)),

                        Rule(_left, Token(SyntaxKind.GreaterThanOrEqualToken, CompletionKind.ScalarInfix), RequiredAdditive,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.GreaterThanOrEqualExpression, left, op, right))
                        ));

            var RequiredRelational =
                Required(Relational, MissingExpression);

            var ExpressionCouple =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    Required(FunctionCallOrPath, MissingExpression),
                    RequiredToken(SyntaxKind.DotDotToken),
                    Required(FunctionCallOrPath, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),

                    (openParen, first, dotDot, second, closeParen) =>
                        new ExpressionCouple(openParen, first, dotDot, second, closeParen));

            var InOperatorExpressionList =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    SeparatedList(Expression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseParenToken),

                    (openParen, list, closeParen) =>
                        new ExpressionList(openParen, list, closeParen));

            var Equality =
                First(
                    Rule(Token(SyntaxKind.AsteriskToken).Hide(), Token(SyntaxKind.EqualEqualToken), Relational, (asterisk, equal, expression) =>
                            (Expression)new BinaryExpression(SyntaxKind.EqualExpression, new StarExpression(asterisk), equal, expression)),

                    ApplyOptional(Relational, _left =>
                        First(
                            Rule(_left, Token(SyntaxKind.EqualEqualToken, CompletionKind.ScalarInfix, CompletionPriority.Top), RequiredRelational,
                                (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.EqualExpression, left, op, right)),

                            Rule(_left, Token(SyntaxKind.BangEqualToken, CompletionKind.ScalarInfix), RequiredRelational,
                                (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.NotEqualExpression, left, op, right)),

                            Rule(_left, Token(SyntaxKind.LessThanGreaterThanToken, CompletionKind.ScalarInfix).Hide(), RequiredRelational,
                                (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.NotEqualExpression, left, op, right)),

                            Rule(_left, Token(SyntaxKind.InKeyword, CompletionKind.ScalarInfix, ctext: SyntaxFacts.GetText(SyntaxKind.InKeyword) + " (|)"), InOperatorExpressionList,
                                (left, op, right) => (Expression)new InExpression(SyntaxKind.InExpression, left, op, right)),

                            Rule(_left, Token(SyntaxKind.InCsKeyword, CompletionKind.ScalarInfix, ctext: SyntaxFacts.GetText(SyntaxKind.InCsKeyword) + " (|)"), InOperatorExpressionList,
                                (left, op, right) => (Expression)new InExpression(SyntaxKind.InCsExpression, left, op, right)),

                            Rule(_left, Token(SyntaxKind.NotInKeyword, CompletionKind.ScalarInfix, ctext: SyntaxFacts.GetText(SyntaxKind.NotInKeyword) + " (|)"), InOperatorExpressionList,
                                (left, op, right) => (Expression)new InExpression(SyntaxKind.NotInExpression, left, op, right)),

                            Rule(_left, Token(SyntaxKind.NotInCsKeyword, CompletionKind.ScalarInfix, ctext: SyntaxFacts.GetText(SyntaxKind.NotInCsKeyword) + " (|)"), InOperatorExpressionList,
                                (left, op, right) => (Expression)new InExpression(SyntaxKind.NotInCsExpression, left, op, right)),

                            Rule(_left, Token(SyntaxKind.HasAnyKeyword, CompletionKind.ScalarInfix, ctext: SyntaxFacts.GetText(SyntaxKind.HasAnyKeyword) + " (|)"), InOperatorExpressionList,
                                (left, op, right) => (Expression)new HasAnyExpression(SyntaxKind.HasAnyKeyword, left, op, right)),

                            Rule(_left, Token(SyntaxKind.BetweenKeyword, CompletionKind.ScalarInfix, ctext: SyntaxFacts.GetText(SyntaxKind.BetweenKeyword) + " (|)"), ExpressionCouple,
                                (left, op, right) => (Expression)new BetweenExpression(SyntaxKind.BetweenExpression, left, op, right)),

                            Rule(_left, Token(SyntaxKind.NotBetweenKeyword, CompletionKind.ScalarInfix, ctext: SyntaxFacts.GetText(SyntaxKind.NotBetweenKeyword) + " (|)"), ExpressionCouple,
                                (left, op, right) => (Expression)new BetweenExpression(SyntaxKind.NotBetweenExpression, left, op, right))
                            )));

            var LogicalAnd =
                ApplyZeroOrMore(Equality, _left =>
                    Rule(_left, Token(SyntaxKind.AndKeyword, CompletionKind.ScalarInfix), Required(Equality, MissingExpression),
                        (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.AndExpression, left, op, right)));

            var LogicalOr =
                ApplyZeroOrMore(LogicalAnd, _left =>
                    Rule(_left, Token(SyntaxKind.OrKeyword, CompletionKind.ScalarInfix), Required(LogicalAnd, MissingExpression),
                        (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.OrExpression, left, op, right)));
            #endregion

            #region Named Parameters
            Parser<LexicalToken, NamedParameter> TokenNamedParameter(string keyword, IReadOnlyList<string> tokenValues = null) =>
                tokenValues != null
                    ? NamedParameter(keyword, First(AsTokenLiteral(Token(tokenValues)), AsTokenLiteral(Token(SyntaxKind.IdentifierToken)).Hide(), ConstantExpression.Hide()), MissingTokenLiteral(tokenValues))
                    : NamedParameter(keyword, First(AsTokenLiteral(Token(SyntaxKind.IdentifierToken)), ConstantExpression.Hide()), MissingTokenLiteral("token"));

            Parser<LexicalToken, NamedParameter> TokenNamedParameterK(SyntaxKind keyword, IReadOnlyList<string> tokenValues = null) =>
                tokenValues != null
                    ? NamedParameter(keyword, First(AsTokenLiteral(Token(tokenValues)), AsTokenLiteral(Token(SyntaxKind.IdentifierToken)).Hide(), ConstantExpression.Hide()), MissingTokenLiteral(tokenValues))
                    : NamedParameter(keyword, First(AsTokenLiteral(Token(SyntaxKind.IdentifierToken)), ConstantExpression.Hide()), MissingTokenLiteral("token"));

            Parser<LexicalToken, NamedParameter> NameReferenceNamedParameterK(SyntaxKind keyword, CompletionHint expressionHint = CompletionHint.None) =>
                NamedParameter(keyword, SimpleNameReference, MissingNameReference, expressionHint);

            Parser<LexicalToken, NamedParameter> NameReferenceNamedParameter(string keyword, CompletionHint expressionHint = CompletionHint.None) =>
                NamedParameter(keyword, SimpleNameReference, MissingNameReference, expressionHint);

            //Parser<LexicalToken, NamedParameter> NameDeclarationNamedParameterK(SyntaxKind keyword, CompletionHint expressionHint = CompletionHint.None) =>
            //    NamedParameter(keyword, SimpleNameDeclarationExpression, MissingNameDeclarationExpression, expressionHint);

            Parser<LexicalToken, NamedParameter> NameDeclarationNamedParameter(string keyword, CompletionHint expressionHint = CompletionHint.None) =>
                NamedParameter(keyword, SimpleNameDeclarationExpression, MissingNameDeclarationExpression, expressionHint);

            Parser<LexicalToken, NamedParameter> NameDeclarationNamedParameterL(IReadOnlyList<string> keywords, CompletionHint expressionHint = CompletionHint.None) =>
                First(keywords.Select(keyword => NameDeclarationNamedParameter(keyword, expressionHint)).ToArray());

            Parser<LexicalToken, NamedParameter> BooleanNamedParameterK(SyntaxKind keyword) =>
                NamedParameter(keyword, First(BooleanLiteralWithCompletion, SimpleNameReference, ConstantExpression.Hide()), MissingBooleanLiteral);

            Parser<LexicalToken, NamedParameter> BooleanNamedParameter(string keyword) =>
                NamedParameter(keyword, First(BooleanLiteralWithCompletion, SimpleNameReference, ConstantExpression.Hide()), MissingBooleanLiteral);

            Parser<LexicalToken, NamedParameter> NumericNamedParameterK(SyntaxKind keyword) =>
                NamedParameter(keyword, First(NumericConstantExpression, SimpleNameReference, ConstantExpression.Hide()), MissingLongLiteral);

            Parser<LexicalToken, NamedParameter> NumericNamedParameter(string keyword) =>
                NamedParameter(keyword, First(NumericConstantExpression, SimpleNameReference, ConstantExpression.Hide()), MissingLongLiteral);

            Parser<LexicalToken, NamedParameter> StringNamedParameterK(SyntaxKind keyword) =>
                NamedParameter(
                    Token(keyword, ctext: $"{SyntaxFacts.GetText(keyword)}=\"|\""),
                    First(StringOrCompoundStringLiteral, SimpleNameReference, ConstantExpression.Hide()),
                    MissingStringLiteral);

            Parser<LexicalToken, NamedParameter> StringNamedParameter(string keyword) =>
                NamedParameter(
                    Token(keyword, ctext: $"{keyword}=\"|\""),
                    First(StringOrCompoundStringLiteral, SimpleNameReference, ConstantExpression.Hide()),
                    MissingStringLiteral);

            var QueryOperatorParameter =
                NamedParameter(
                    Token(KnownQueryOperatorParameterNames).Hide(),
                    First(
                        AsTokenLiteral(Token(SyntaxKind.IdentifierToken)),
                        BooleanLiteral,
                        NumericConstantExpression,
                        StringOrCompoundStringLiteral)).Hide();
            #endregion

            #region Query Operators
            var ConsumeOperator =
                Rule(
                    Token(SyntaxKind.ConsumeKeyword, CompletionKind.QueryPrefix).Hide(),
                    List(First(
                        BooleanNamedParameter("decodeblocks"),
                        QueryOperatorParameter)),
                    (consume, parameters) =>
                        (QueryOperator)new ConsumeOperator(consume, parameters))
                .WithTag("<consume>");

            var CountAsIdentifierClause =
                Rule(
                    Token(SyntaxKind.AsKeyword).Hide(),
                    RequiredToken(SyntaxKind.IdentifierToken),
                    (asKeyword, identifier) =>
                        new CountAsIdentifierClause(asKeyword, identifier));

            var CountOperator =
                Rule(
                    Token(SyntaxKind.CountKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    Optional(CountAsIdentifierClause),
                    (countKeyword, asIdentifier) =>
                        (QueryOperator)new CountOperator(countKeyword, asIdentifier))
                .WithTag("<count>");

            var ExecuteAndCacheOperator =
                Rule(
                    Token(SyntaxKind.ExecuteAndCacheKeyword, CompletionKind.QueryPrefix),
                    (keyword) =>
                        (QueryOperator)new ExecuteAndCacheOperator(keyword))
                .WithTag("<execute-and-cache>");

            var ExtendOperator =
                Rule(
                    Token(SyntaxKind.ExtendKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    SeparatedList<Expression>(NamedExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, list) =>
                        (QueryOperator)new ExtendOperator(keyword, list))
                .WithTag("<extend>");

            var FacetWithClause =
                First(
                    Rule(Token(SyntaxKind.WithKeyword), Token(SyntaxKind.OpenParenToken), Required(ForkPipeExpression, MissingQueryOperatorExpression), RequiredToken(SyntaxKind.CloseParenToken),
                        (withKeyword, openParen, expr, closeParen) =>
                            (FacetWithClause)new FacetWithExpressionClause(withKeyword, openParen, expr, closeParen)),

                    Rule(Token(SyntaxKind.WithKeyword), Required(ForkPipeOperator, MissingQueryOperator),
                        (withKeyword, op) =>
                            (FacetWithClause)new FacetWithOperatorClause(withKeyword, op)));

            var FacetOperator =
                Rule(
                    Token(SyntaxKind.FacetKeyword, CompletionKind.QueryPrefix).Hide(),
                    RequiredToken(SyntaxKind.ByKeyword),
                    SeparatedList<Expression>(EntityReferenceExpression, SyntaxKind.CommaToken, missingElement: MissingNameReferenceNode, oneOrMore: true),
                    Optional(FacetWithClause),
                    (facetKeyword, byKeyword, list, withClause) =>
                        (QueryOperator)new FacetOperator(facetKeyword, byKeyword, list, withClause))
                .WithTag("<facet>");

            var FilterOperator =
                Rule(
                    First(
                        Token(SyntaxKind.WhereKeyword, CompletionKind.QueryPrefix, CompletionPriority.Top),
                        Token(SyntaxKind.FilterKeyword).Hide()),
                    List(QueryOperatorParameter),
                    Required(NamedExpression, MissingExpression),
                    (keyword, parameters, condition) =>
                        (QueryOperator)new FilterOperator(keyword, parameters, condition))
                .WithTag("<filter>");

            var GetSchemaOperator =
                Rule(
                    Token(SyntaxKind.GetSchemaKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    keyword => (QueryOperator)new GetSchemaOperator(keyword))
                .WithTag("<get-schema>");

            Parser<LexicalToken, DataScopeClause> DataScopeClause(CompletionKind ckind) =>
                Rule(
                    Token(SyntaxKind.DataScopeKeyword, ckind).Hide(),
                    RequiredToken(SyntaxKind.EqualToken),
                    RequiredToken(KustoFacts.DataScopeValues),
                    (dataScopeKeyword, equalToken, valueToken) =>
                        new DataScopeClause(dataScopeKeyword, equalToken, valueToken));

            var FindOperand =
                First(
                    WildcardedEntityReference,
                    BrackettedEntityNamePathElementSelector,
                    BarePathElementSelector);

            var FindInClause =
                Rule(
                    Token(SyntaxKind.InKeyword),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    SeparatedList(FindOperand, SyntaxKind.CommaToken, MissingExpressionNode, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (inKeyword, openParen, exprs, closeParen) =>
                        new FindInClause(inKeyword, openParen, exprs, closeParen));

            var ColumnNameReference =
                First(
                    IdentifierNameReference,
                    KeywordNameReference,
                    BrackettedNameReference,
                    ClientParameterReference)
                .WithTag("<column>");

            var TypedColumnNameReference =
                ApplyOptional(
                    ColumnNameReference,
                    _left => Rule(
                        _left,
                        Token(SyntaxKind.ColonToken),
                        Required(First(ParamTypeExtended, IdentifierTypeExpression), MissingType),
                        (name, colon, type) => (Expression)new TypedColumnReference((NameReference)name, colon, type)));

            var PackExpression =
                Rule(
                    Token(SyntaxKind.PackKeyword),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    RequiredToken(SyntaxKind.AsteriskToken, CompletionKind.Syntax),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (pack, openParen, asterisk, closeParen) =>
                        (Expression)new PackExpression(pack, openParen, asterisk, closeParen));

            var FindProjectColumn =
                First(
                    PackExpression,
                    StarExpression,
                    TypedColumnNameReference);

            var FindProjectClause =
                First(
                    Rule(
                        Token(SyntaxKind.ProjectKeyword),
                        SeparatedList(FindProjectColumn, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                        (token, list) => new FindProjectClause(token, list)),
                    Rule(Token(SyntaxKind.ProjectSmartKeyword),
                        (token) => new FindProjectClause(token, SyntaxList<SeparatedElement<Expression>>.Empty())));

            var FindProjectAwayClause =
                Rule(
                    Token(SyntaxKind._ProjectAwayKeyword),
                    SeparatedList(FindProjectColumn, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (token, list) => new FindProjectClause(token, list));

            var FindOperator =
                Rule(
                    Token(SyntaxKind.FindKeyword, CompletionKind.QueryPrefix),
                    Optional(DataScopeClause(CompletionKind.Syntax)),
                    List(First(
                        NameDeclarationNamedParameter(KustoFacts.FindWithSourceProperty),
                        QueryOperatorParameter)),
                    Optional(FindInClause),
                    Optional(Token(SyntaxKind.WhereKeyword)),
                    Required(UnnamedExpression, MissingExpression), // condition
                    Optional(FindProjectClause),
                    Optional(FindProjectAwayClause).Hide(), // internal use?
                    (findKeyword, dataScope, parameters, inClause, whereKeyword, condition, project, projectAway) =>
                        (QueryOperator)new FindOperator(
                            findKeyword,
                            dataScope,
                            parameters,
                            inClause,
                            whereKeyword ?? ((parameters.ChildCount > 0 || inClause != null) ? CreateMissingToken(SyntaxKind.WhereKeyword) : null),
                            condition,
                            project,
                            projectAway))
                .WithTag("<find>");

            var SearchPredicate =
                First(
                    UnnamedExpression,
                    ApplyOptional(
                        StarExpression,
                        _left =>
                            Rule(
                                _left,
                                Token(SyntaxKind.AndKeyword),
                                Required(UnnamedExpression, MissingExpression),
                                (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.AndExpression, left, op, right))));

            var SearchOperator =
                Rule(
                    Token(SyntaxKind.SearchKeyword, CompletionKind.QueryPrefix),
                    List(First(
                        TokenNamedParameter("kind", KustoFacts.SearchKinds),
                        QueryOperatorParameter // allow parsing of any known query parameter
                        )),
                    Optional(DataScopeClause(CompletionKind.Syntax)),
                    Optional(FindInClause),
                    Required(SearchPredicate, MissingExpression),
                    (keyword, parameters, dataScope, inClause, predicate) =>
                        (QueryOperator)new SearchOperator(keyword, parameters, dataScope, inClause, predicate))
                .WithTag("<search>");

            var NameEqualsClause =
                If(Or(Token(SyntaxKind.IdentifierToken), Token(SyntaxKind.OpenBracketToken)),
                    Rule(SimpleNameDeclaration, RequiredToken(SyntaxKind.EqualToken),
                        (name, equalsToken) => new NameEqualsClause(name, equalsToken)));

            var ForkExpression =
                Rule(
                    Optional(NameEqualsClause),
                    Token(SyntaxKind.OpenParenToken),
                    Required(ForkPipeExpression, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (nameClause, openParen, expr, closeParen) =>
                        new ForkExpression(nameClause, openParen, expr, closeParen));

            var ForkOperator =
                Rule(
                    Token(SyntaxKind.ForkKeyword, CompletionKind.QueryPrefix).Hide(),
                    List(ForkExpression, missingElement: MissingForkExpressionNode, oneOrMore: true),
                    (forkKeyword, list) => (QueryOperator)new ForkOperator(forkKeyword, list))
                .WithTag("<fork>");

            var PartitionQueryExpression =
               Rule(
                   Token(SyntaxKind.OpenBraceToken),
                   Required(Expression, MissingExpression),
                   RequiredToken(SyntaxKind.CloseBraceToken),
                   (openParen, expr, closeParen) =>
                       new PartitionExpression(openParen, expr, closeParen));

            var PartitionSubqueryExpression =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    Required(PartitionPipeExpression, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (openParen, expr, closeParen) =>
                        new PartitionExpression(openParen, expr, closeParen));

            var PartitionOperator =
                Rule(
                    Token(SyntaxKind.PartitionKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    List(
                        First(
                            TokenNamedParameterK(SyntaxKind.HintDotSpreadKeyword, KustoFacts.JoinHintRemotes), // TODO: this can be number
                            TokenNamedParameterK(SyntaxKind.HintDotConcurrencyKeyword, KustoFacts.JoinHintRemotes), // TODO: this can be number
                            BooleanNamedParameterK(SyntaxKind.HintDotMaterializedKeyword),
                            QueryOperatorParameter // allow parsing of any known query parameter
                             )),
                    RequiredToken(SyntaxKind.ByKeyword),
                    Required(EntityReferenceExpression, MissingNameReference),
                    Required(First(PartitionSubqueryExpression, PartitionQueryExpression), MissingPartitionSubqueryExpression),
                    (partitionKeyword, parameters, byKeyword, byExpression, operand) =>
                        (QueryOperator)new PartitionOperator(partitionKeyword, parameters, byKeyword, byExpression, operand))
                .WithTag("<partition>");

            var JoinEqualityExpression =
                ApplyOptional(
                    FunctionCallOrPath,
                    _left =>
                        Rule(
                            _left,
                            Token(SyntaxKind.EqualEqualToken, CompletionKind.ScalarInfix), RequiredFunctionCallOrPath,
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.EqualExpression, left, op, right)));

            var JoinAndExpression =
                ApplyZeroOrMore(
                    JoinEqualityExpression,
                    _left =>
                        Rule(_left, Token(SyntaxKind.AndKeyword, CompletionKind.ScalarInfix), Required(JoinEqualityExpression, MissingExpression),
                            (left, op, right) => (Expression)new BinaryExpression(SyntaxKind.AndExpression, left, op, right)));

            var JoinOnExpression =
                Best(
                    JoinAndExpression, // only legal join expressions will show in intellisense
                    UnnamedExpression.Hide()); // otherwise parse any expression and tag it in semantic analysis.

            var JoinOnClause =
                Rule(
                    Token(SyntaxKind.OnKeyword),
                    SeparatedList(JoinOnExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (onKeyword, list) => (JoinConditionClause)new JoinOnClause(onKeyword, list));

            var JoinWhereClause =
                Rule(
                    Token(SyntaxKind.WhereKeyword),
                    Required(UnnamedExpression, MissingExpression),
                    (keyword, predicate) => (JoinConditionClause)new JoinWhereClause(keyword, predicate));

            var JoinOperator =
                Rule(
                    Token(SyntaxKind.JoinKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    List(
                        First(
                            TokenNamedParameterK(SyntaxKind.KindKeyword, KustoFacts.JoinKinds),
                            TokenNamedParameterK(SyntaxKind.HintDotRemoteKeyword, KustoFacts.JoinHintRemotes),
                            NameReferenceNamedParameterK(SyntaxKind.HintDotShuffleKeyKeyword, expressionHint: CompletionHint.Column),
                            TokenNamedParameterK(SyntaxKind.HintDotStrategyKeyword, KustoFacts.JoinHintStrategies),
                            NumericNamedParameterK(SyntaxKind.HintDotNumPartitions),
                            QueryOperatorParameter // allow parsing of any known query parameter
                            )),
                    Required(UnnamedExpression, MissingExpression),
                    Optional(First(
                        JoinOnClause,
                        JoinWhereClause.Hide())),
                    (joinKeyword, parameters, expr, condition) =>
                        (QueryOperator)new JoinOperator(joinKeyword, parameters, expr, condition))
                .WithTag("<join>");

            var LookupOperator =
                Rule(
                    Token(SyntaxKind.LookupKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    List(
                        TokenNamedParameterK(SyntaxKind.KindKeyword, KustoFacts.JoinKinds)),
                    Required(UnnamedExpression, MissingExpression),
                    Required(JoinOnClause, MissingJoinOnClause),
                    (LookupKeyword, parameters, expr, onClause) =>
                        (QueryOperator)new LookupOperator(LookupKeyword, parameters, expr, onClause))
                .WithTag("<lookup>");

            var MakeSeriesOnClause =
                Rule(
                    RequiredToken(SyntaxKind.OnKeyword),
                    Required(NamedExpression, MissingExpression),
                    (keyword, expr) => new MakeSeriesOnClause(keyword, expr));

            var MakeSeriesInRangeClause =
                Rule(
                    RequiredToken(SyntaxKind.InKeyword),
                    RequiredToken(SyntaxKind.RangeKeyword),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    SeparatedList(NamedExpression, SyntaxKind.CommaToken, MissingExpressionNode),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (inKeyword, rangeKeyword, openParen, list, closeParen) =>
                        (MakeSeriesRangeClause)new MakeSeriesInRangeClause(inKeyword, rangeKeyword, new ExpressionList(openParen, list, closeParen)));

            var MakeSeriesFromToStepClause =
                Rule(
                    Token(SyntaxKind.FromKeyword),
                    Required(UnnamedExpression, MissingExpression),
                    RequiredToken(SyntaxKind.ToKeyword),
                    Required(UnnamedExpression, MissingExpression),
                    RequiredToken(SyntaxKind.StepKeyword),
                    Required(UnnamedExpression, MissingExpression),
                    (FromToken, fromEx, ToToken, toEx, stepToken, stepEx) =>
                        (MakeSeriesRangeClause)new MakeSeriesFromToStepClause(FromToken, fromEx, ToToken, toEx, stepToken, stepEx));

            var MakeSeriesByClause =
                Rule(
                    Token(SyntaxKind.ByKeyword),
                    SeparatedList(NamedExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, list) => new MakeSeriesByClause(keyword, list));

            var DefaultExpressionClause =
                Rule(
                    Token(SyntaxKind.DefaultKeyword),
                    RequiredToken(SyntaxKind.EqualToken),
                    Required(NamedExpression, MissingExpression),
                    (defaultKeyword, equalToken, expr) =>
                        new DefaultExpressionClause(defaultKeyword, equalToken, expr));

            var MakeSeriesExpression =
                Rule(
                    NamedExpression,
                    Optional(DefaultExpressionClause),
                    (agg, defexp) => new MakeSeriesExpression(agg, defexp));

            var MakeSeriesOperator =
                Rule(
                    Token(SyntaxKind.MakeSeriesKeyword, CompletionKind.QueryPrefix),
                    List(QueryOperatorParameter),
                    SeparatedList(MakeSeriesExpression, SyntaxKind.CommaToken, MissingMakeSeriesExpressionNode, oneOrMore: true),
                    MakeSeriesOnClause,
                    First(MakeSeriesFromToStepClause, MakeSeriesInRangeClause),
                    Optional(MakeSeriesByClause),
                    (keyword, parameters, aggregates, onClause, rangeClause, byClause) =>
                        (QueryOperator)new MakeSeriesOperator(keyword, parameters, aggregates, onClause, rangeClause, byClause))
                .WithTag("<make-series>");

            var ToTypeOfClause =
                Rule(
                    Token(SyntaxKind.ToKeyword),
                    Required(TypeofLiteral, MissingTypeOfLiteral),
                    (toKeyword, typeOfLiteral) => new ToTypeOfClause(toKeyword, (TypeOfLiteralExpression)typeOfLiteral));

            var MvExpandExpression =
                First(
                    // check for missing initial expression error case
                    If(Token(SyntaxKind.ToKeyword).Hide(),
                        Rule(ToTypeOfClause,
                            (clause) =>
                                new MvExpandExpression((Expression)MissingExpressionNode.Clone(), clause))),
                    Rule(NamedExpression, Optional(ToTypeOfClause),
                        (expr, toTypeOfClause) =>
                            new MvExpandExpression(expr, toTypeOfClause)));

            var MvExpandExpressionList =
                First(
                    // if only one item that is just "to typeof(xxx)" then allow expression to be null w/o error
                    If(And(Token(SyntaxKind.ToKeyword).Hide(), TypeofLiteral, Fails(Token(SyntaxKind.CommaToken))),
                        Rule(ToTypeOfClause,
                            clause => new SyntaxList<SeparatedElement<MvExpandExpression>>(new[] {
                                new SeparatedElement<MvExpandExpression>(new MvExpandExpression(null, clause)) }))),
                    SeparatedList(MvExpandExpression, SyntaxKind.CommaToken, missingElement: MissingMvExpandExpressionNode, oneOrMore: true));

            var MvExpandRowLimitClause =
                Rule(
                    Token(SyntaxKind.LimitKeyword),
                    Required(UnnamedExpression, MissingExpression),
                    (keyword, expr) => new MvExpandRowLimitClause(keyword, expr));

            var MvExpandParameters =
                First(
                    TokenNamedParameter(KustoFacts.MvExpandBagExpansionProperty, KustoFacts.MvExpandBagExpansions).Hide(),
                    TokenNamedParameterK(SyntaxKind.KindKeyword, KustoFacts.MvExpandBagExpansions),
                    NameDeclarationNamedParameter(KustoFacts.MvExpandWithItemIndexProperty),
                    QueryOperatorParameter);

            var MvExpandOperator =
                Rule(
                    First(
                        Token(SyntaxKind.MvExpandKeyword, CompletionKind.QueryPrefix).Hide(),
                        Token(SyntaxKind.MvDashExpandKeyword, CompletionKind.QueryPrefix)),
                    List(MvExpandParameters),
                    MvExpandExpressionList,
                    Optional(MvExpandRowLimitClause),
                    (keyword, parameters, list, rowLimit) =>
                        (QueryOperator)new MvExpandOperator(keyword, parameters, list, rowLimit))
                .WithTag("<mvexpand>");

            var MvApplyExpression =
                First(
                    // check for missing initial expression error case
                    If(Token(SyntaxKind.ToKeyword).Hide(),
                        Rule(ToTypeOfClause,
                            (clause) =>
                                new MvApplyExpression((Expression)MissingExpressionNode.Clone(), clause))),
                    Rule(NamedExpression, Optional(ToTypeOfClause),
                        (expr, toTypeOfClause) =>
                            new MvApplyExpression(expr, toTypeOfClause)));

            var MvApplyExpressionList =
                First(
                    // if only one item that is just "to typeof(xxx)" then allow expression to be null w/o error
                    If(And(Token(SyntaxKind.ToKeyword).Hide(), TypeofLiteral, Fails(Token(SyntaxKind.CommaToken))),
                        Rule(ToTypeOfClause,
                            clause => new SyntaxList<SeparatedElement<MvApplyExpression>>(new[] {
                                new SeparatedElement<MvApplyExpression>(new MvApplyExpression(null, clause)) }))),
                    SeparatedList(MvApplyExpression, SyntaxKind.CommaToken, missingElement: MissingMvApplyExpressionNode, oneOrMore: true));

            var MvApplyRowLimitClause =
                Rule(
                    Token(SyntaxKind.LimitKeyword),
                    Required(UnnamedExpression, MissingExpression),
                    (keyword, expr) => new MvApplyRowLimitClause(keyword, expr));

            var MvApplyContextIdClause =
              Rule(
                  Token(SyntaxKind.IdKeyword),
                  Required(UnnamedExpression, MissingExpression),
                  (keyword, expr) => new MvApplyContextIdClause(keyword, expr));

            var MvApplyParameters =
                First(
                    NameDeclarationNamedParameter(KustoFacts.MvApplyWithItemIndexProperty),
                    QueryOperatorParameter);

            var MvApplySubqueryExpression =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    Required(PartitionPipeExpression, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (openParen, expr, closeParen) =>
                        new MvApplySubqueryExpression(openParen, expr, closeParen));

            var MvApplyOperator =
                Rule(
                    First(
                        Token(SyntaxKind.MvApplyKeyword, CompletionKind.QueryPrefix).Hide(),
                        Token(SyntaxKind.MvDashApplyKeyword, CompletionKind.QueryPrefix)),
                    List(MvApplyParameters).Hide(),
                    MvApplyExpressionList,
                    Optional(MvApplyRowLimitClause),
                    Optional(MvApplyContextIdClause).Hide(),
                    RequiredToken(SyntaxKind.OnKeyword),
                    Required(MvApplySubqueryExpression, MissingMvApplySubqueryExpression),
                    (keyword, parameters, list, rowLimit, contextId, onKeyword, subquery) =>
                        (QueryOperator)new MvApplyOperator(keyword, parameters, list, rowLimit, contextId, onKeyword, subquery))
                .WithTag("<mvapply>");

            var EvaluateOperator =
                Rule(
                    Token(SyntaxKind.EvaluateKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    List(First(
                            TokenNamedParameterK(SyntaxKind.HintDotDistributionKeyword, KustoFacts.DistributionHintStrategies),
                            QueryOperatorParameter)),
                    Required(FunctionCall, MissingFunctionCallExpression),
                    (keyword, parameters, expr) => (QueryOperator)new EvaluateOperator(keyword, parameters, (FunctionCallExpression)expr))
                .WithTag("<evaluate>");

            var ParseParameter =
                First(
                    TokenNamedParameterK(SyntaxKind.KindKeyword, KustoFacts.ParseKinds),
                    TokenNamedParameterK(SyntaxKind.FlagsKeyword),
                    QueryOperatorParameter);

            var ParseParameters =
                List(ParseParameter);

            var NameAndOptionalTypeDeclaration =
                First(
                    ApplyOptional(
                        SimpleNameDeclarationExpression,
                        _left =>
                            Rule(
                                _left,
                                Token(SyntaxKind.ColonToken),
                                Required(First(ParamTypeExtended, IdentifierTypeExpression), MissingType),
                                (name, colon, type) =>
                                    (Expression)new NameAndTypeDeclaration((NameDeclaration)name, colon, type))),
                    Rule(
                        Token(SyntaxKind.ColonToken).Hide(),
                        Required(First(ParamTypeExtended, IdentifierTypeExpression), MissingType),
                        (colon, type) =>
                            (Expression)new NameAndTypeDeclaration((NameDeclaration)MissingNameDeclarationNode.Clone(), colon, type)));

            var ParseWithExpression =
                First(
                    StarExpression,
                    StringOrCompoundStringLiteral,
                    NameAndOptionalTypeDeclaration);

            var ParseOperator =
                Rule(
                    Token(SyntaxKind.ParseKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    ParseParameters,
                    Required(UnnamedExpression, MissingExpression),
                    RequiredToken(SyntaxKind.WithKeyword),
                    List(Rule(ParseWithExpression, e => (SyntaxNode)e)),
                    (parseKeyword, parameters, expr, withKeyword, expressions) =>
                        (QueryOperator)new ParseOperator(parseKeyword, parameters, expr, withKeyword, expressions))
                .WithTag("<parse>");

            var ProjectOperator =
                Rule(
                    Token(SyntaxKind.ProjectKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    SeparatedList(NamedExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, list) => (QueryOperator)new ProjectOperator(keyword, list))
                .WithTag("<project>");

            var ProjectAwayOperator =
                Rule(
                    Token(SyntaxKind.ProjectAwayKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    SeparatedList(SimpleOrWildcardedEntityReference, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, list) => (QueryOperator)new ProjectAwayOperator(keyword, list))
                .WithTag("<project-away>");

            var ProjectRenameOperator =
                Rule(
                    Token(SyntaxKind.ProjectRenameKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    SeparatedList(NamedExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, list) => (QueryOperator)new ProjectRenameOperator(keyword, list))
                .WithTag("<project-rename>");

            var SampleOperator =
                Rule(
                    Token(SyntaxKind.SampleKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    List(QueryOperatorParameter),
                    Required(NamedExpression, MissingExpression),
                    (sampleKeyword, parameters, expression) => (QueryOperator)new SampleOperator(sampleKeyword, parameters, expression))
                .WithTag("<sample>");

            var SampleDistinctOperator =
                Rule(
                    Token(SyntaxKind.SampleDistinctKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    List(QueryOperatorParameter),
                    Required(NamedExpression, MissingExpression),
                    RequiredToken(SyntaxKind.OfKeyword),
                    Required(NamedExpression, MissingExpression),
                    (keyword, parameters, expr, ofKeyword, ofExpr) =>
                        (QueryOperator)new SampleDistinctOperator(keyword, parameters, expr, ofKeyword, ofExpr))
                .WithTag("<sample-distinct>");

            var ReduceByWithParameter =
                First(
                    NumericNamedParameterK(SyntaxKind.ThresholdKeyword),
                    StringNamedParameterK(SyntaxKind.CharactersKeyword),
                    QueryOperatorParameter);

            var ReduceByWithClause =
                Rule(
                    Token(SyntaxKind.WithKeyword),
                    SeparatedList(ReduceByWithParameter, SyntaxKind.CommaToken, MissingNamedParameterNode),
                    (keyword, list) => new ReduceByWithClause(keyword, list));

            var ReduceByOperator =
                Rule(
                    Token(SyntaxKind.ReduceKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    List(First(
                        TokenNamedParameterK(SyntaxKind.KindKeyword, KustoFacts.ReduceByKinds),
                        QueryOperatorParameter)),
                    RequiredToken(SyntaxKind.ByKeyword),
                    Required(NamedExpression, MissingExpression),
                    Optional(ReduceByWithClause),
                    (reduceKeyword, parameters, byKeyword, expr, withClause) =>
                        (QueryOperator)new ReduceByOperator(reduceKeyword, parameters, byKeyword, expr, withClause))
                .WithTag("<reduce-by>");

            var SummarizeByClause =
                Rule(
                    Token(SyntaxKind.ByKeyword),
                    SeparatedList(
                        If(Not(And(Token(SyntaxKind.BinKeyword), Token(SyntaxKind.EqualToken))), NamedExpression),
                        SyntaxKind.CommaToken,
                        missingElement: MissingExpressionNode,
                        oneOrMore: true),
                    (byKeyword, expressions) =>
                        new SummarizeByClause(byKeyword, expressions))
                .WithTag("<summarize-by>");

            var SummarizeOperator =
                Rule(
                    Token(SyntaxKind.SummarizeKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    List(First(
                        NameReferenceNamedParameterK(SyntaxKind.HintDotShuffleKeyKeyword, expressionHint: CompletionHint.Column),
                        NumericNamedParameterK(SyntaxKind.HintDotNumPartitions),
                        QueryOperatorParameter)),
                    SeparatedList(NamedExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: false),
                    Optional(SummarizeByClause),
                    (summarizeKeyword, parameters, aggregates, byClause) =>
                        (QueryOperator)new SummarizeOperator(summarizeKeyword, parameters, aggregates, byClause))
                .WithTag("<summarize>");

            var DistinctOperator =
                Rule(
                    Token(SyntaxKind.DistinctKeyword, CompletionKind.QueryPrefix),
                    SeparatedList(First(StarExpression, UnnamedExpression), SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, list) => (QueryOperator)new DistinctOperator(keyword, list))
                .WithTag("<distinct>");

            var TakeOperator =
                Rule(
                    First(
                        Token(SyntaxKind.LimitKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                        Token(SyntaxKind.TakeKeyword, CompletionKind.QueryPrefix)),
                    List(QueryOperatorParameter),
                    Required(NamedExpression.Examples(KustoFacts.LimitExamples), MissingExpression),
                    (keyword, parameters, expression) =>
                        (QueryOperator)new TakeOperator(keyword, parameters, expression))
                .WithTag("<take>");

            var MissingFirstOrLastToken =
                SyntaxToken.Missing("", SyntaxKind.FirstKeyword, new[] { DiagnosticFacts.GetMissingFirstOrLast() });

            var OrderingNullsClause =
                Rule(
                    Token(SyntaxKind.NullsKeyword),
                    Required(First(Token(SyntaxKind.FirstKeyword), Token(SyntaxKind.LastKeyword)), () => MissingFirstOrLastToken.Clone()),
                    (keyword, firstOrLast) => new OrderingNullsClause(keyword, firstOrLast));

            var OrderingClause =
                Rule(
                    Optional(Token(new[] { SyntaxKind.AscKeyword, SyntaxKind.DescKeyword })),
                    Optional(OrderingNullsClause),
                    (ascOrDesc, nullsClause) => new OrderingClause(ascOrDesc, nullsClause));

            var OrderingNoNullClause =
               Rule(
                   Optional(Token(new[] { SyntaxKind.AscKeyword, SyntaxKind.DescKeyword })),
                   (ascOrDesc) => new OrderingClause(ascOrDesc, null));

            var SortExpression =
                ApplyOptional(
                    NamedExpression,
                    _left =>
                        Rule(
                            _left,
                            If(Or(Token(SyntaxKind.AscKeyword), Token(SyntaxKind.DescKeyword), Token(SyntaxKind.NullsKeyword)),
                                OrderingClause),
                            (left, right) => (Expression)new OrderedExpression(left, right)));

            var SortOperator =
                Rule(
                    First(
                        Token(SyntaxKind.OrderKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                        Token(SyntaxKind.SortKeyword, CompletionKind.QueryPrefix, CompletionPriority.High)),
                    RequiredToken(SyntaxKind.ByKeyword),
                    SeparatedList(SortExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, byKeyword, list) =>
                        (QueryOperator)new SortOperator(keyword, byKeyword, list))
                .WithTag("<sort>");

            var ReorderExpression =
                ApplyOptional(
                    SimpleOrWildcardedEntityReference,
                    _left =>
                        Rule(
                            _left,
                            If(Or(Token(SyntaxKind.AscKeyword), Token(SyntaxKind.DescKeyword)),
                                OrderingNoNullClause),
                            (left, right) => (Expression)new OrderedExpression(left, right)));

            var ProjectReorderOperator =
                Rule(
                   Token(SyntaxKind.ProjectReorderKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                   SeparatedList(ReorderExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                   (keyword, list) => (QueryOperator)new ProjectReorderOperator(keyword, list))
                .WithTag("<project-reorder>");

            var TopHittersByClause =
                Rule(
                    Token(SyntaxKind.ByKeyword),
                    Required(NamedExpression, MissingExpression),
                    (keyword, expression) => new TopHittersByClause(keyword, expression));

            var TopHittersOperator =
                Rule(
                    Token(SyntaxKind.TopHittersKeyword, CompletionKind.QueryPrefix),
                    Required(NamedExpression.Examples(KustoFacts.TopExamples), MissingExpression),
                    RequiredToken(SyntaxKind.OfKeyword),
                    Required(NamedExpression, MissingExpression),
                    Optional(TopHittersByClause),
                    (keyword, expr, ofKeyword, ofExpr, byClause) =>
                        (QueryOperator)new TopHittersOperator(keyword, expr, ofKeyword, ofExpr, byClause))
                .WithTag("<top-hitters>");

            var TopOperator =
                Rule(
                    Token(SyntaxKind.TopKeyword, CompletionKind.QueryPrefix),
                    List(
                        First(
                            BooleanNamedParameterK(SyntaxKind.HintDotProgressiveTopKeyword)
                             )),
                    Required(NamedExpression.Examples(KustoFacts.TopExamples), MissingExpression),
                    RequiredToken(SyntaxKind.ByKeyword),
                    Required(SortExpression, MissingExpression),
                    (keyword, parameters, expr, byKeyword, byExpr) =>
                        (QueryOperator)new TopOperator(keyword, parameters, expr, byKeyword, byExpr))
                .WithTag("<top>");

            var TopNestedWithOthersClause =
                Rule(
                    Token(SyntaxKind.WithKeyword),
                    RequiredToken(SyntaxKind.OthersKeyword),
                    RequiredToken(SyntaxKind.EqualToken),
                    ConstantExpression,
                    (withKeyword, othersKeyword, equals, expression) =>
                        new TopNestedWithOthersClause(withKeyword, othersKeyword, equals, expression));

            var TopNestedByExpression =
                ApplyOptional(
                    NamedExpression,
                    _left =>
                        Rule(
                            _left,
                            If(Or(Token(SyntaxKind.AscKeyword), Token(SyntaxKind.DescKeyword), Token(SyntaxKind.NullsKeyword)),
                                OrderingClause),
                            (expr, orderingClause) => (Expression)new OrderedExpression(expr, orderingClause)));

            var TopNestedClause =
                Rule(
                    Token(SyntaxKind.TopNestedKeyword),
                    Optional(NamedExpression),
                    RequiredToken(SyntaxKind.OfKeyword),
                    Required(NamedExpression, MissingExpression),
                    Optional(TopNestedWithOthersClause),
                    RequiredToken(SyntaxKind.ByKeyword),
                    Required(TopNestedByExpression, MissingExpression),

                    (keyword, expr, ofKeyword, ofExpr, withOthersClause, byKeyword, byExpr) =>
                        new TopNestedClause(keyword, expr, ofKeyword, ofExpr, withOthersClause, byKeyword, byExpr));

            var MissingTopNestedClauseNode =
                new TopNestedClause(
                    SyntaxToken.Missing(SyntaxKind.TopNestedKeyword),
                    expression: null,
                    ofKeyword: SyntaxToken.Missing(SyntaxKind.OfKeyword),
                    ofExpression: new NameReference(SyntaxToken.Missing(SyntaxKind.IdentifierToken)),
                    withOthersClause: null,
                    byKeyword: SyntaxToken.Missing(SyntaxKind.ByKeyword),
                    byExpression: new NameReference(SyntaxToken.Missing(SyntaxKind.IdentifierToken)),
                    diagnostics: new[] { DiagnosticFacts.GetMissingClause() }
                    );

            var TopNestedOperator =
                If(Token(SyntaxKind.TopNestedKeyword, CompletionKind.QueryPrefix),
                    Rule(SeparatedList(TopNestedClause, SyntaxKind.CommaToken, MissingTopNestedClauseNode, oneOrMore: true),
                        list => (QueryOperator)new TopNestedOperator(list)))
                .WithTag("<top-nested>");

            var UnionExpression =
                First(
                    ParenthesizedExpression,
                    WildcardedEntityReference,
                    BrackettedEntityNamePathElementSelector,
                    BarePathElementSelector);

            var UnionOperator =
                Rule(
                    Token(SyntaxKind.UnionKeyword, CompletionKind.QueryPrefix),
                    List(First(
                        TokenNamedParameterK(SyntaxKind.KindKeyword, KustoFacts.UnionKinds),
                        NameDeclarationNamedParameterL(KustoFacts.UnionWithSourceProperties),
                        BooleanNamedParameterK(SyntaxKind.IsFuzzyKeyword),
                        QueryOperatorParameter)),
                    SeparatedList<Expression>(UnionExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, parameters, list) => (QueryOperator)new UnionOperator(keyword, parameters, list))
                .WithTag("<union>");

            var AsOperator =
                Rule(
                    Token(SyntaxKind.AsKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    List(QueryOperatorParameter),
                    Required(SimpleNameDeclaration, MissingNameDeclaration),
                    (keyword, parameters, name) => (QueryOperator)new AsOperator(keyword, parameters, name))
                .WithTag("<as>");

            var SerializeOperator =
                Rule(
                    Token(SyntaxKind.SerializeKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    List(QueryOperatorParameter),
                    SeparatedList(NamedExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: false),
                    (keyword, parameters, exprs) =>
                        (QueryOperator)new SerializeOperator(keyword, parameters, exprs))
                .WithTag("<serialize>");

            var RangeOperator =
                // don't parse as range operator if it looks like the range function
                If(And(Token(SyntaxKind.RangeKeyword, CompletionKind.QueryPrefix), Fails(Token("("))),
                    Rule(
                        Token(SyntaxKind.RangeKeyword, CompletionKind.QueryPrefix),
                        Required(SimpleNameDeclaration, MissingNameDeclaration),
                        RequiredToken(SyntaxKind.FromKeyword),
                        Required(UnnamedExpression, MissingExpression),
                        RequiredToken(SyntaxKind.ToKeyword),
                        Required(UnnamedExpression, MissingExpression),
                        RequiredToken(SyntaxKind.StepKeyword),
                        Required(UnnamedExpression, MissingExpression),
                        (rangeToken, name, FromToken, fromEx, ToToken, toEx, stepToken, stepEx) =>
                            (QueryOperator)new RangeOperator(rangeToken, name, FromToken, fromEx, ToToken, toEx, stepToken, stepEx)))
                .WithTag("<range>");

            var InvokeOperator =
                Rule(
                    Token(SyntaxKind.InvokeKeyword, CompletionKind.QueryPrefix, CompletionPriority.Low),
                    Required(DotCompositeFunctionCall, MissingExpression),
                    (keyword, function) => (QueryOperator)new InvokeOperator(keyword, function))
                .WithTag("<invoke>");

            var RenderNameListName =
                If(Not(Token(KustoFacts.ChartProperties)), SimpleNameDeclaration);

            var RenderNameList =
                Rule(
                    Produce(
                        Sequence(
                            Rule(RenderNameListName, n => (SyntaxElement)n),
                            ZeroOrMore(
                                Sequence(
                                    Rule(Token(SyntaxKind.CommaToken), t => (SyntaxElement)t),
                                    Rule(RenderNameListName, l => (SyntaxElement)l)))),
                        elements => MakeSeparatedList<NameDeclaration>(elements)),
                    list => (Expression)new RenderNameList(list));

            var RenderProperty =
                First(
                    StringNamedParameter("title"),
                    NameReferenceNamedParameter("xcolumn", CompletionHint.Column),
                    NamedParameter("series", RenderNameList, expressionHint: CompletionHint.Column),
                    NamedParameter("ycolumns", RenderNameList, expressionHint: CompletionHint.Column),
                    NamedParameter(Token("anomalycolumns"), RenderNameList, expressionHint: CompletionHint.Column),
                    TokenNamedParameter("kind", KustoFacts.ChartKinds),
                    StringNamedParameter("xtitle"),
                    StringNamedParameter("ytitle"),
                    TokenNamedParameter("xaxis", KustoFacts.ChartAxis),
                    TokenNamedParameter("yaxis", KustoFacts.ChartAxis),
                    TokenNamedParameter("legend", KustoFacts.ChartLegends),
                    TokenNamedParameter("ysplit", KustoFacts.ChartYSplit),
                    BooleanNamedParameter("accumulate"),
                    NumericNamedParameter("ymin"),
                    NumericNamedParameter("ymax"),
                    NamedParameter(Token(SyntaxKind.IdentifierToken), Required(ConstantExpression, MissingExpression)).Hide());

            var RenderWithClause =
                Rule(
                    Token(SyntaxKind.WithKeyword).Hide(),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    SeparatedList(RenderProperty, SyntaxKind.CommaToken, MissingNamedParameterNode, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseParenToken),

                    (withKeyword, openParen, properties, closeParen) =>
                        new RenderWithClause(withKeyword, openParen, properties, closeParen));

            var RenderChartType =
                First(
                    First(KustoFacts.ChartTypes.Select(c => KustoFacts.HiddenChartTypes.Contains(c) ? Token(c).Hide() : Token(c, CompletionKind.RenderChart)).ToArray()),
                    Token(SyntaxKind.IdentifierToken)); // allow any identifier as a chart type and flag it later during semantic analysis (binding)

            var RenderOperator =
                Rule(
                    Token(SyntaxKind.RenderKeyword, CompletionKind.QueryPrefix, CompletionPriority.High),
                    Required(RenderChartType, () => CreateMissingToken(KustoFacts.ChartTypes)),
                    Optional(RenderWithClause),
                    (keyword, chart, withClause) => (QueryOperator)new RenderOperator(keyword, chart, withClause, null))
                .WithTag("<render>");

            var PrintOperator =
                Rule(
                    Token(SyntaxKind.PrintKeyword, CompletionKind.QueryPrefix),
                    SeparatedList(NamedExpression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    (keyword, exprs) => (QueryOperator)new PrintOperator(keyword, exprs))
                .WithTag("<print>");

            var PrePipeQueryOperator =
                First(
                    FindOperator,
                    SearchOperator,
                    UnionOperator,
                    RangeOperator,
                    PrintOperator);

            var BadQueryOperator =
                Rule(
                    Token(SyntaxKind.IdentifierToken),
                    id => (QueryOperator)new BadQueryOperator((SyntaxToken)id, new[] { DiagnosticFacts.GetQueryOperatorExpected() }));

            var PostPipeQueryOperator =
                First(
                    ConsumeOperator,
                    CountOperator,
                    ExecuteAndCacheOperator,
                    ExtendOperator,
                    FacetOperator,
                    FilterOperator,
                    GetSchemaOperator,
                    JoinOperator,
                    ForkOperator,
                    LookupOperator,
                    MakeSeriesOperator,
                    MvApplyOperator,
                    MvExpandOperator,
                    EvaluateOperator,
                    ParseOperator,
                    PartitionOperator,
                    ProjectOperator,
                    SampleOperator,
                    SampleDistinctOperator,
                    ProjectAwayOperator,
                    ProjectRenameOperator,
                    ProjectReorderOperator,
                    ReduceByOperator,
                    SummarizeOperator,
                    DistinctOperator,
                    TakeOperator,
                    SortOperator,
                    TopHittersOperator,
                    TopOperator,
                    TopNestedOperator,
                    UnionOperator,
                    RenderOperator,
                    AsOperator,
                    SerializeOperator,
                    InvokeOperator);

            ForkPipeOperatorCore =
                First(
                    CountOperator,
                    ExtendOperator,
                    FilterOperator,
                    ParseOperator,
                    TakeOperator,
                    TopNestedOperator,
                    ProjectOperator,
                    ProjectAwayOperator,
                    ProjectRenameOperator,
                    ProjectReorderOperator,
                    SummarizeOperator,
                    DistinctOperator,
                    TopHittersOperator,
                    TopOperator,
                    SortOperator,
                    MvExpandOperator,
                    MvApplyOperator,
                    ReduceByOperator,
                    SampleOperator,
                    SampleDistinctOperator,
                    AsOperator,
                    InvokeOperator,
                    ExecuteAndCacheOperator);

            ForkPipeExpressionCore =
                ApplyZeroOrMore(
                    Rule(ForkPipeOperator, o => (Expression)o),
                    _left =>
                        Rule(_left, Token(SyntaxKind.BarToken), Required(ForkPipeOperator, MissingQueryOperator),
                            (left, pipeToken, right) => (Expression)new PipeExpression(left, pipeToken, right)));

            PartitionPipeOperatorCore =
                First(
                    CountOperator,
                    ExtendOperator,
                    FilterOperator,
                    ParseOperator,
                    TakeOperator,
                    TopNestedOperator,
                    ProjectOperator,
                    ProjectAwayOperator,
                    ProjectRenameOperator,
                    ProjectReorderOperator,
                    SummarizeOperator,
                    DistinctOperator,
                    TopHittersOperator,
                    TopOperator,
                    SortOperator,
                    MvExpandOperator,
                    MvApplyOperator,
                    ReduceByOperator,
                    SampleOperator,
                    SampleDistinctOperator,
                    AsOperator,
                    InvokeOperator,
                    ExecuteAndCacheOperator);

            PartitionPipeExpressionCore =
                ApplyZeroOrMore(
                    Rule(PartitionPipeOperator, o => (Expression)o),
                    _left =>
                        Rule(_left, Token(SyntaxKind.BarToken), Required(PartitionPipeOperator, MissingQueryOperator),
                            (left, pipeToken, right) => (Expression)new PipeExpression(left, pipeToken, right)));

            var InitialPipeElementExpression =
                First(
                    Rule(PrePipeQueryOperator, o => (Expression)o),
                    Rule(PostPipeQueryOperator, o => (Expression)o).Hide(), // allow these to parse, but fail in binding
                    UnnamedExpression);

            this.FollowingPipeElementExpression =
                First(
                    PostPipeQueryOperator,
                    PrePipeQueryOperator.Hide(), // allow these to parse, but fail in binding
                    BadQueryOperator.Hide()); // allow these to parse, but fail in binding

            var PipeExpression =
                ApplyZeroOrMore(
                    InitialPipeElementExpression,
                    _left =>
                        Rule(_left, Token(SyntaxKind.BarToken), Required(FollowingPipeElementExpression, MissingQueryOperator),
                            (left, op, right) => (Expression)new PipeExpression(left, op, right)));

            UnnamedExpressionCore =
                LogicalOr;

            ExpressionCore =
                PipeExpression;
            #endregion

            #region Statements
            var AliasStatement =
                Rule(
                    Token(SyntaxKind.AliasKeyword, CompletionKind.QueryPrefix).Hide(),
                    RequiredToken(SyntaxKind.DatabaseKeyword),
                    Required(SimpleNameDeclaration, MissingNameDeclaration),
                    RequiredToken(SyntaxKind.EqualToken),
                    Required(UnnamedExpression, MissingExpression),
                    (aliasKeyword, databaseKeyword, name, equalToken, expression) =>
                        (Statement)new AliasStatement(aliasKeyword, databaseKeyword, name, equalToken, expression))
                .WithTag("<alias>");

            var MaterializeExpression =
                Rule(
                    Token(SyntaxKind.MaterializeKeyword),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    Required(PipeExpression, MissingExpression),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (keyword, openParen, expr, closeParen) =>
                        (Expression)new MaterializeExpression(keyword, openParen, expr, closeParen));

            var DefaultValueDeclaration =
                Rule(
                    Token(SyntaxKind.EqualToken),
                    Required(First(ConstantExpression, NameTokenLiteral), MissingExpression),
                    (equalToken, value) => new DefaultValueDeclaration(equalToken, value));

            var FunctionParameter =
                Rule(
                    NameAndTypeDeclaration,
                    Optional(DefaultValueDeclaration),
                    (nameAndType, defaultValue) => new FunctionParameter(nameAndType, defaultValue));

            this.FunctionParameters =
                Rule(
                    RequiredToken(SyntaxKind.OpenParenToken),
                    SeparatedList(FunctionParameter, SyntaxKind.CommaToken, missingElement: MissingFunctionParameterNode, oneOrMore: false),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (openParen, parameters, closeParen) => new FunctionParameters(openParen, parameters, closeParen));

            var FunctionBodyStatement =
                First(
                    Rule(LetStatement, RequiredToken(SyntaxKind.SemicolonToken),
                        (statement, semicolon) => new SeparatedElement<Statement>(statement, semicolon)),
                    Rule(QueryParametersStatement, RequiredToken(SyntaxKind.SemicolonToken),
                        (statement, semicolon) => new SeparatedElement<Statement>(statement, semicolon)));

            var FunctionBodyStatementList =
                List(FunctionBodyStatement, missingElement: MissingStatementElementNode, oneOrMore: false)
                .WithTag("<statement-list>");

            this.FunctionBody =
                Rule(
                    RequiredToken(SyntaxKind.OpenBraceToken),
                    FunctionBodyStatementList,
                    Optional(Expression),
                    Optional(Token(SyntaxKind.SemicolonToken)),
                    RequiredToken(SyntaxKind.CloseBraceToken),
                    (openBrace, statements, expr, semicolon, closeBrace) =>
                        new FunctionBody(openBrace, statements, expr, semicolon, closeBrace));

            var FunctionDeclaration =
                Rule(
                    Optional(Token(SyntaxKind.ViewKeyword)),
                    FunctionParameters,
                    FunctionBody,
                    (view, parameters, body) => new FunctionDeclaration(view, parameters, body))
                .WithTag("<function-declaration>");

            LetStatementCore =
                First(
                    // looks like let with function declaration?
                    If(
                        And(Token(SyntaxKind.LetKeyword, CompletionKind.QueryPrefix),
                            ScanSimpleName,
                            Token(SyntaxKind.EqualToken),
                            Optional(Token(SyntaxKind.ViewKeyword)),
                            Token(SyntaxKind.OpenParenToken),
                            Or(Token(SyntaxKind.CloseParenToken), Token(SyntaxKind.AsteriskToken), And(ScanSimpleName, Token(SyntaxKind.ColonToken)))),
                        Rule(
                            Token(SyntaxKind.LetKeyword),
                            SimpleNameDeclaration,
                            Token(SyntaxKind.EqualToken),
                            FunctionDeclaration,
                            (letKeyword, name, equal, expression) =>
                                (Statement)new LetStatement(letKeyword, name, equal, expression))),
                    // let with materialize?
                    If(
                        And(
                            Token(SyntaxKind.LetKeyword, CompletionKind.QueryPrefix),
                            ScanSimpleName,
                            Token(SyntaxKind.EqualToken),
                            Token(SyntaxKind.MaterializeKeyword)),
                        Rule(
                            Token(SyntaxKind.LetKeyword),
                            Required(SimpleNameDeclaration, MissingNameDeclaration),
                            Token(SyntaxKind.EqualToken),
                            MaterializeExpression,
                            (keyword, name, equal, expr) =>
                                (Statement)new LetStatement(keyword, name, equal, expr))),
                    // otherwise regular let statement
                    Rule(
                        Token(SyntaxKind.LetKeyword, CompletionKind.QueryPrefix),
                        Required(SimpleNameDeclaration, MissingNameDeclaration),
                        RequiredToken(SyntaxKind.EqualToken),
                        Required(Expression, MissingExpression),
                        (letKeyword, name, equalToken, expression) =>
                            (Statement)new LetStatement(letKeyword, name, equalToken, expression)));

            var OptionValueClause =
                Rule(
                    Token(SyntaxKind.EqualToken),
                    Required(UnnamedExpression, MissingExpression),
                    (equal, expr) => new OptionValueClause(equal, expr));

            var SetOptionStatement =
                Rule(
                    Token(SyntaxKind.SetKeyword, CompletionKind.QueryPrefix),
                    Required(SimpleNameDeclaration, MissingNameDeclaration),
                    Optional(OptionValueClause),
                    (keyword, name, value) =>
                        (Statement)new SetOptionStatement(keyword, name, value))
                .WithTag("<set-option>");

            QueryParametersStatementCore =
                Rule(
                    Token(SyntaxKind.DeclareKeyword, CompletionKind.QueryPrefix).Hide(),
                    Token(SyntaxKind.QueryParametersKeyword),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    SeparatedList(FunctionParameter, SyntaxKind.CommaToken, missingElement: MissingFunctionParameterNode, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (declareKeyword, queryParametersKeyword, open, list, close) =>
                        (Statement)new QueryParametersStatement(declareKeyword, queryParametersKeyword, open, list, close));

            var Restriction =
                First(
                    If(ScanWildcardedEntityReference, WildcardedEntityReference),
                    SimpleNameReference);

            var RestrictStatement =
                Rule(
                    Token(SyntaxKind.RestrictKeyword, CompletionKind.QueryPrefix).Hide(),
                    RequiredToken(SyntaxKind.AccessKeyword),
                    RequiredToken(SyntaxKind.ToKeyword),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    SeparatedList<Expression>(Restriction, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (restrictKeyword, accessKeyword, toKeyword, openParen, list, closeParen) =>
                        (Statement)new RestrictStatement(restrictKeyword, accessKeyword, toKeyword, openParen, list, closeParen))
                .WithTag("<restrict>");

            var PatternPathValue =
                Rule(
                    Token(SyntaxKind.DotToken),
                    RequiredToken(SyntaxKind.OpenBracketToken),
                    Required(StringLiteral, MissingStringLiteral),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    (dot, openBracket, value, closeBracket) =>
                        new PatternPathValue(dot, openBracket, value, closeBracket));

            var PatternMatchStatementElement =
                Rule(
                    LetStatement,
                    RequiredToken(SyntaxKind.SemicolonToken),
                    (statement, semicolon) =>
                        new SeparatedElement<Statement>(statement, semicolon));

            var PatternMatchBody =
                Rule(
                    RequiredToken(SyntaxKind.OpenBraceToken),
                    List(PatternMatchStatementElement, MissingStatementElementNode, oneOrMore: false),
                    Optional(Expression),
                    Optional(Token(SyntaxKind.SemicolonToken)),
                    RequiredToken(SyntaxKind.CloseBraceToken),
                    (open, statements, expression, semi, close) =>
                        new FunctionBody(open, statements, expression, semi, close));

            var PatternMatchValue =
                First(
                    StringLiteral,
                    UnnamedExpression.Hide());

            var PatternMatch =
                Rule(
                    Token(SyntaxKind.OpenParenToken),
                    SeparatedList<Expression>(PatternMatchValue, SyntaxKind.CommaToken, missingElement: MissingStringLiteralNode, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    Optional(PatternPathValue),
                    RequiredToken(SyntaxKind.EqualToken),
                    PatternMatchBody,
                    RequiredToken(SyntaxKind.SemicolonToken),
                    (openParen, exprs, closeParen, path, equalToken, body, semicolon) =>
                        new PatternMatch(new Syntax.ExpressionList(openParen, exprs, closeParen), path, equalToken, body, semicolon));

            var PatternPathParameter =
                Rule(
                    Token(SyntaxKind.OpenBracketToken),
                    Required(NameAndTypeDeclaration, () => (NameAndTypeDeclaration)MissingNameAndTypeDeclarationNode.Clone()),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    (openBracket, parameter, closeBracket) =>
                        new PatternPathParameter(openBracket, parameter, closeBracket));

            var MissingPatternMatch =
                new PatternMatch(
                    new ExpressionList(
                        SyntaxToken.Missing(SyntaxKind.OpenParenToken),
                        SyntaxList<SeparatedElement<Expression>>.Empty(),
                        SyntaxToken.Missing(SyntaxKind.CloseParenToken)),
                    null, // path value okay to be null
                    SyntaxToken.Missing(SyntaxKind.EqualToken),
                    new FunctionBody(
                        SyntaxToken.Missing(SyntaxKind.OpenBraceToken),
                        SyntaxList<SeparatedElement<Statement>>.Empty(),
                        null, // expression okay to be null
                        null, // semicolon okay to be null
                        SyntaxToken.Missing(SyntaxKind.CloseBraceToken)),
                    SyntaxToken.Missing(SyntaxKind.SemicolonToken),
                    new[] { DiagnosticFacts.GetMissingPatternMatch() });

            var PatternDeclaration =
                Rule(
                    Token(SyntaxKind.EqualToken),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    SeparatedList(NameAndTypeDeclaration, SyntaxKind.CommaToken, missingElement: MissingNameAndTypeDeclarationNode, oneOrMore: false),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    Optional(PatternPathParameter),
                    RequiredToken(SyntaxKind.OpenBraceToken),
                    List(PatternMatch, missingElement: MissingPatternMatch, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseBraceToken),

                    (equal, openParen, parameters, closeParen, pathParameter, openBrace, patterns, closeBrace) =>
                        new PatternDeclaration(equal, openParen, parameters, closeParen, pathParameter, openBrace, patterns, closeBrace));

            var PatternStatement =
                Rule(
                    Token(SyntaxKind.DeclareKeyword, CompletionKind.QueryPrefix).Hide(),
                    Token(SyntaxKind.PatternKeyword),
                    Required(SimpleNameDeclaration, MissingNameDeclaration),
                    Optional(PatternDeclaration),
                    (declareKeyword, patternKeyword, name, pattern) =>
                        (Statement)new PatternStatement(declareKeyword, patternKeyword, name, pattern))
                .WithTag("<pattern-statement>");

            var QueryStatement =
                Rule(
                    Expression,
                    expr => (Statement)new ExpressionStatement(expr));

            var PrimaryPathSelector =
                ApplyOptional(
                    PathElementSelector,
                    _left =>
                        Rule(
                            _left,
                            DataScopeClause(CompletionKind.TabularSuffix),
                            (path, clause) =>
                                (Expression)new DataScopeExpression(path, clause)));

            var DataTableExpression =
                Rule(
                    Token(SyntaxKind.DataTableKeyword, CompletionKind.QueryPrefix),
                    SchemaMultipartType,
                    RequiredToken(SyntaxKind.OpenBracketToken),
                    SeparatedList(ConstantExpression, SyntaxKind.CommaToken, MissingExpressionNode, allowTrailingSeparator: true),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    (keyword, schema, openBracket, values, closeBracket) =>
                        (Expression)new DataTableExpression(keyword, schema, openBracket, values, closeBracket));

            var ExternalDataWithClauseNamedParameter =
                Rule(
                    SimpleNameDeclaration,
                    RequiredToken(SyntaxKind.EqualToken),
                    Required(
                        First(Literal, AsTokenLiteral(Token(SyntaxKind.IdentifierToken)), KeywordTokenLiteral),
                        MissingValue),
                    (name, equalToken, value) =>
                        new NamedParameter(name, equalToken, value));

            var ExternalDataWithClause =
                Rule(
                    Token(SyntaxKind.WithKeyword),
                    RequiredToken(SyntaxKind.OpenParenToken),
                    SeparatedList(ExternalDataWithClauseNamedParameter, SyntaxKind.CommaToken, MissingNamedParameterNode),
                    RequiredToken(SyntaxKind.CloseParenToken),
                    (keyword, openParen, list, closeParen) =>
                        new ExternalDataWithClause(keyword, openParen, list, closeParen));

            var ExternalDataExpression =
                Rule(
                    Token(SyntaxKind.ExternalDataKeyword, CompletionKind.QueryPrefix),
                    List(QueryOperatorParameter),
                    SchemaMultipartType,
                    RequiredToken(SyntaxKind.OpenBracketToken),
                    SeparatedList(Expression, SyntaxKind.CommaToken, missingElement: MissingExpressionNode, allowTrailingSeparator: true, oneOrMore: true),
                    RequiredToken(SyntaxKind.CloseBracketToken),
                    Optional(ExternalDataWithClause),
                    (keyword, parameters, schema, openBracket, name, closeBracket, withClause) =>
                        (Expression)new ExternalDataExpression(keyword, parameters, schema, openBracket, name, closeBracket, withClause));

            PrimaryExpressionCore =
                First(
                    ConstantExpression,
                    ParenthesizedExpression,
                    DataTableExpression,
                    ExternalDataExpression,
                    PrimaryPathSelector);

            this.Statement =
                First(
                    AliasStatement,
                    LetStatement,
                    SetOptionStatement,
                    QueryParametersStatement,
                    RestrictStatement,
                    PatternStatement,
                    QueryStatement)
                .WithTag("<statement>");
            #endregion

            #region QueryBlock
            this.StatementList =
                SeparatedList(Statement, SyntaxKind.SemicolonToken, missingElement: MissingStatementNode, allowTrailingSeparator: true);

            this.SkippedTokens =
                If(AnyTokenButEnd,
                    Rule(
                        List(AnyTokenButEnd), // consumes all remaining tokens
                        tokens => new SkippedTokens(tokens)))
                    .WithTag("<skipped-tokens>");

            this.QueryBlock =
                Rule(
                    StatementList,
                    Optional(SkippedTokens),
                    Optional(Token(SyntaxKind.EndOfTextToken)),

                    (statements, skipped, end) =>
                        new QueryBlock(statements, skipped, end));
            #endregion
        }

        #region Missing Elements
        public static readonly NameDeclaration MissingNameDeclarationNode =
            new NameDeclaration(SyntaxToken.Missing(SyntaxKind.IdentifierToken), new[] { DiagnosticFacts.GetMissingName() });

        public static readonly Func<NameDeclaration> MissingNameDeclaration =
            () => (NameDeclaration)MissingNameDeclarationNode.Clone();

        public static readonly Func<Expression> MissingNameDeclarationExpression =
            () => (NameDeclaration)MissingNameDeclarationNode.Clone();

        public static readonly NameReference MissingNameReferenceNode =
            new NameReference(SyntaxToken.Missing(SyntaxKind.IdentifierToken), new[] { DiagnosticFacts.GetMissingName() });

        public static readonly Func<Expression> MissingNameReference =
            () => (NameReference)MissingNameReferenceNode.Clone();

        public static readonly Expression MissingExpressionNode =
            new NameReference(SyntaxToken.Missing(SyntaxKind.IdentifierToken), new[] { DiagnosticFacts.GetMissingExpression() });

        public static readonly Func<Expression> MissingExpression =
            () => (Expression)MissingExpressionNode.Clone();

        public static readonly Expression MissingValueNode =
            new NameReference(SyntaxToken.Missing(SyntaxKind.IdentifierToken), new[] { DiagnosticFacts.GetMissingValue() });

        public static readonly Func<Expression> MissingValue =
            () => (Expression)MissingValueNode.Clone();

        public static readonly TypeExpression MissingTypeNode =
            new PrimitiveTypeExpression(SyntaxToken.Missing(SyntaxKind.IdentifierToken), new[] { DiagnosticFacts.GetMissingTypeName() });

        public static readonly Func<TypeExpression> MissingType =
            () => (TypeExpression)MissingTypeNode.Clone();

        public static readonly Expression MissingLongLiteralNode =
            new LiteralExpression(SyntaxKind.LongLiteralExpression, SyntaxToken.Missing(SyntaxKind.LongLiteralToken), new[] { DiagnosticFacts.GetMissingNumber() });

        public static readonly Func<Expression> MissingLongLiteral =
            () => (Expression)MissingLongLiteralNode.Clone();

        public static readonly Expression MissingStringLiteralNode =
            new LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxToken.Missing(SyntaxKind.StringLiteralToken), new[] { DiagnosticFacts.GetMissingString() });

        public static readonly Func<Expression> MissingStringLiteral =
            () => (Expression)MissingStringLiteralNode.Clone();

        public static readonly Expression MissingBooleanLiteralNode =
            new LiteralExpression(SyntaxKind.BooleanLiteralExpression, SyntaxToken.Missing(SyntaxKind.BooleanLiteralToken), new[] { DiagnosticFacts.GetMissingBoolean() });

        public static readonly Func<Expression> MissingBooleanLiteral =
            () => (Expression)MissingBooleanLiteralNode.Clone();

        public static readonly TypeOfLiteralExpression MissingTypeofLiteralNode =
            new TypeOfLiteralExpression(
                SyntaxToken.Missing(SyntaxKind.TypeOfKeyword),
                SyntaxToken.Missing(SyntaxKind.OpenParenToken),
                SyntaxList<SeparatedElement<Expression>>.Empty(),
                SyntaxToken.Missing(SyntaxKind.CloseParenToken),
                new[] { DiagnosticFacts.GetMissingTypeOfLiteral() });

        public static readonly Func<Expression> MissingTypeOfLiteral =
            () => (Expression)MissingTypeofLiteralNode.Clone();

        public static readonly Expression MissingJsonValueNode =
            new LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxToken.Missing(SyntaxKind.StringLiteralToken),
                new[] { DiagnosticFacts.GetMissingJsonValue() });

        public static readonly Func<Expression> MissingJsonValue =
            () => (Expression)MissingJsonValueNode.Clone();

        public static readonly JsonPair MissingJsonPairNode =
            new JsonPair(
                SyntaxToken.Missing(SyntaxKind.StringLiteralToken),
                SyntaxToken.Missing(SyntaxKind.ColonToken),
                new LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxToken.Missing(SyntaxKind.StringLiteralToken)),
                new[] { DiagnosticFacts.GetMissingJsonPair() });

        public static readonly JoinOnClause MissingJoinOnClauseNode =
            new JoinOnClause(
                SyntaxToken.Missing(SyntaxKind.JoinOnClause),
                SyntaxList<SeparatedElement<Expression>>.Empty(),
                new[] { DiagnosticFacts.GetMissingJoinOnClause() });

        public static readonly Func<JoinConditionClause> MissingJoinOnClause =
            () => (JoinOnClause)MissingJoinOnClauseNode.Clone();

        public static readonly Func<JsonPair> MissingJsonPair =
            () => (JsonPair)MissingJsonPairNode.Clone();

        public static readonly FunctionCallExpression MissingFunctionCallNode =
            new FunctionCallExpression(
                new NameReference(SyntaxToken.Missing(SyntaxKind.IdentifierToken)),
                new ExpressionList(
                    SyntaxToken.Missing(SyntaxKind.OpenParenToken),
                    SyntaxList<SeparatedElement<Expression>>.Empty(),
                    SyntaxToken.Missing(SyntaxKind.CloseParenToken)),
                new[] { DiagnosticFacts.GetMissingFunctionCall() });

        public static readonly Func<FunctionCallExpression> MissingFunctionCall =
            () => (FunctionCallExpression)MissingFunctionCallNode.Clone();

        public static readonly Func<Expression> MissingFunctionCallExpression =
            () => (FunctionCallExpression)MissingFunctionCallNode.Clone();

        public static readonly QueryOperator MissingQueryOperatorNode =
            new BadQueryOperator(SyntaxToken.Missing(SyntaxKind.IdentifierToken), new[] { DiagnosticFacts.GetQueryOperatorExpected() });

        public static readonly Func<QueryOperator> MissingQueryOperator =
            () => (QueryOperator)MissingQueryOperatorNode.Clone();

        public static readonly Func<Expression> MissingQueryOperatorExpression =
            () => (QueryOperator)MissingQueryOperatorNode.Clone();

        public static readonly MakeSeriesExpression MissingMakeSeriesExpressionNode =
            new MakeSeriesExpression((Expression)MissingExpressionNode.Clone(), null);

        public static readonly Func<MakeSeriesExpression> MissingMakeSeriesExpression =
            () => (MakeSeriesExpression)MissingMakeSeriesExpressionNode.Clone();

        public static readonly MvExpandExpression MissingMvExpandExpressionNode =
            new MvExpandExpression((Expression)MissingExpressionNode.Clone(), null);

        public static readonly Func<MvExpandExpression> MissingMvExpandExpression =
            () => (MvExpandExpression)MissingMvExpandExpressionNode.Clone();

        public static readonly MvApplyExpression MissingMvApplyExpressionNode =
           new MvApplyExpression((Expression)MissingExpressionNode.Clone(), null);

        public static readonly Func<MvApplyExpression> MissingMvApplyExpression =
            () => (MvApplyExpression)MissingMvApplyExpressionNode.Clone();

        public static readonly MvApplySubqueryExpression MissingMvApplySubqueryExpressionNode =
            new MvApplySubqueryExpression(
                CreateMissingToken(SyntaxKind.OpenParenToken),
                (Expression)MissingExpressionNode.Clone(),
                CreateMissingToken(SyntaxKind.CloseParenToken));

        public static readonly Func<MvApplySubqueryExpression> MissingMvApplySubqueryExpression =
            () => (MvApplySubqueryExpression)MissingMvApplySubqueryExpressionNode.Clone();

        public static readonly ForkExpression MissingForkExpressionNode =
            new ForkExpression(
                null,
                CreateMissingToken(SyntaxKind.OpenParenToken),
                (Expression)MissingExpressionNode.Clone(),
                CreateMissingToken(SyntaxKind.CloseParenToken));

        public static readonly Func<ForkExpression> MissingForkExpression =
            () => (ForkExpression)MissingForkExpressionNode.Clone();

        public static readonly PartitionExpression MissingPartitionSubqueryExpressionNode =
            new PartitionExpression(
                CreateMissingToken(SyntaxKind.OpenParenToken),
                (Expression)MissingExpressionNode.Clone(),
                CreateMissingToken(SyntaxKind.CloseParenToken));

        public static readonly Func<PartitionExpression> MissingPartitionSubqueryExpression =
            () => (PartitionExpression)MissingPartitionSubqueryExpressionNode.Clone();

        public static readonly PartitionExpression MissingPartitionQueryExpressionNode =
            new PartitionExpression(
                CreateMissingToken(SyntaxKind.OpenBraceToken),
                (Expression)MissingExpressionNode.Clone(),
                CreateMissingToken(SyntaxKind.CloseBraceToken));

        public static readonly Func<PartitionExpression> MissingPartitionQueryExpression =
            () => (PartitionExpression)MissingPartitionQueryExpressionNode.Clone();

        public static readonly Statement MissingStatementNode =
            new ExpressionStatement(
                new NameReference(SyntaxToken.Missing(SyntaxKind.IdentifierToken)),
                new[] { DiagnosticFacts.GetMissingStatement() });

        public static readonly Func<Statement> MissingStatement =
            () => (Statement)MissingStatementNode.Clone();

        public static readonly SeparatedElement<Statement> MissingStatementElementNode =
            new SeparatedElement<Statement>((Statement)MissingStatementNode.Clone());

        public static readonly Func<SeparatedElement<Statement>> MissingStatementElement =
            () => (SeparatedElement<Statement>)MissingStatementElementNode.Clone();

        public static readonly NameAndTypeDeclaration MissingNameAndTypeDeclarationNode =
            new NameAndTypeDeclaration(
                new NameDeclaration(SyntaxToken.Missing(SyntaxKind.IdentifierToken)),
                SyntaxToken.Missing(SyntaxKind.ColonToken),
                new PrimitiveTypeExpression(SyntaxToken.Missing(SyntaxKind.StringKeyword)),
                new[] { DiagnosticFacts.GetMissingParameter() });

        public static readonly Func<NameAndTypeDeclaration> MissingNameAndTypeDeclaration =
            () => (NameAndTypeDeclaration)MissingNameAndTypeDeclarationNode.Clone();

        public static readonly FunctionParameter MissingFunctionParameterNode =
            new FunctionParameter(
                new NameAndTypeDeclaration(
                    new NameDeclaration(SyntaxToken.Missing(SyntaxKind.IdentifierToken)),
                    SyntaxToken.Missing(SyntaxKind.ColonToken),
                    new PrimitiveTypeExpression(SyntaxToken.Missing(SyntaxKind.StringKeyword))),
                 null,
                 new[] { DiagnosticFacts.GetMissingParameter() });

        public static readonly Func<FunctionParameter> MissingFunctionParameter =
            () => (FunctionParameter)MissingFunctionParameterNode.Clone();

        public static readonly NamedParameter MissingNamedParameterNode =
            new NamedParameter(
                new NameDeclaration(SyntaxToken.Missing(SyntaxKind.IdentifierToken)),
                SyntaxToken.Missing(SyntaxKind.EqualToken),
                new NameReference(SyntaxToken.Missing(SyntaxKind.IdentifierToken)),
                CompletionHint.None,
                new[] { DiagnosticFacts.GetMissingParameter() });

        public static readonly Func<NamedParameter> MissingNamedParameter =
            () => (NamedParameter)MissingNamedParameterNode.Clone();

        public static FunctionDeclaration MissingFunctionDeclarationNode =
            new FunctionDeclaration(null,
                new FunctionParameters(
                    SyntaxToken.Missing(SyntaxKind.OpenParenToken),
                    SyntaxList<SeparatedElement<FunctionParameter>>.Empty(),
                    SyntaxToken.Missing(SyntaxKind.CloseParenToken)),
                new FunctionBody(
                    SyntaxToken.Missing(SyntaxKind.OpenBraceToken),
                    SyntaxList<SeparatedElement<Statement>>.Empty(),
                    null,
                    null,
                    SyntaxToken.Missing(SyntaxKind.CloseBraceToken)),
                new[] { DiagnosticFacts.GetMissingFunctionDeclaration() });

        public static Func<FunctionDeclaration> MissingFunctionDeclaration =
            () => (FunctionDeclaration)MissingFunctionDeclarationNode.Clone();

        public static Expression MissingTokenLiteralNode(IReadOnlyList<string> tokens) =>
            new LiteralExpression(SyntaxKind.TokenLiteralExpression,
                SyntaxToken.Missing(SyntaxKind.IdentifierToken), new[] { DiagnosticFacts.GetTokenExpected(tokens) });

        public static Func<Expression> MissingTokenLiteral(IReadOnlyList<string> tokens)
        {
            var diagnostic = DiagnosticFacts.GetTokenExpected(tokens);
            return () => new LiteralExpression(SyntaxKind.TokenLiteralExpression,
                SyntaxToken.Missing(SyntaxKind.IdentifierToken), new[] { diagnostic });
        }

        public static Func<Expression> MissingTokenLiteral(params string[] tokens) =>
            MissingTokenLiteral((IReadOnlyList<string>)tokens);
        #endregion

        #region other
        private static Parser<LexicalToken, NameDeclaration> AsIdentifierNameDeclaration(Parser<LexicalToken, SyntaxToken> tokenParser) =>
            Rule(tokenParser, (keyword) => new NameDeclaration(keyword));

        private static Parser<LexicalToken, Expression> AsIdentifierNameReference(Parser<LexicalToken, SyntaxToken> tokenParser) =>
            Rule(tokenParser, (keyword) => (Expression)new NameReference(keyword));

        private static Parser<LexicalToken, TypeExpression> AsPrimitiveTypeExpression(Parser<LexicalToken, SyntaxToken> tokenParser) =>
            Rule(tokenParser,
                typeToken => (TypeExpression)new PrimitiveTypeExpression(typeToken));

        private static Parser<LexicalToken, Expression> AsTokenLiteral(Parser<LexicalToken, SyntaxToken> tokenParser) =>
            Rule(tokenParser, (token) => (Expression)new LiteralExpression(SyntaxKind.TokenLiteralExpression, token));

        public static readonly Dictionary<SyntaxKind, SyntaxKind> StringOperatorMap = new Dictionary<SyntaxKind, SyntaxKind>
            {
                { SyntaxKind.EqualTildeToken, SyntaxKind.EqualTildeExpression },
                { SyntaxKind.BangTildeToken, SyntaxKind.BangTildeExpression },
                { SyntaxKind.HasKeyword, SyntaxKind.HasExpression },
                { SyntaxKind.ColonToken, SyntaxKind.SearchExpression },
                { SyntaxKind.NotHasKeyword, SyntaxKind.NotHasExpression },
                { SyntaxKind.HasCsKeyword, SyntaxKind.HasCsExpression },
                { SyntaxKind.NotHasCsKeyword, SyntaxKind.NotHasCsExpression },
                { SyntaxKind.HasPrefixKeyword, SyntaxKind.HasPrefixExpression },
                { SyntaxKind.NotHasPrefixKeyword, SyntaxKind.NotHasPrefixExpression },
                { SyntaxKind.HasPrefixCsKeyword,  SyntaxKind.HasPrefixCsExpression },
                { SyntaxKind.NotHasPrefixCsKeyword, SyntaxKind.NotHasPrefixCsExpression },
                { SyntaxKind.HasSuffixKeyword, SyntaxKind.HasSuffixExpression },
                { SyntaxKind.NotHasSuffixKeyword, SyntaxKind.NotHasSuffixExpression },
                { SyntaxKind.HasSuffixCsKeyword, SyntaxKind.HasSuffixCsExpression },
                { SyntaxKind.NotHasSuffixCsKeyword, SyntaxKind.NotHasSuffixCsExpression },
                { SyntaxKind.LikeKeyword, SyntaxKind.LikeExpression },
                { SyntaxKind.NotLikeKeyword, SyntaxKind.NotLikeExpression },
                { SyntaxKind.LikeCsKeyword, SyntaxKind.LikeCsExpression },
                { SyntaxKind.NotLikeCsKeyword, SyntaxKind.NotLikeCsExpression },
                { SyntaxKind.ContainsKeyword, SyntaxKind.ContainsExpression },
                { SyntaxKind.NotContainsKeyword, SyntaxKind.NotContainsExpression },
                { SyntaxKind.NotBangContainsKeyword, SyntaxKind.NotContainsExpression },
                { SyntaxKind.ContainsCsKeyword, SyntaxKind.ContainsCsExpression },
                { SyntaxKind.Contains_CsKeyword, SyntaxKind.ContainsCsExpression },
                { SyntaxKind.NotContainsCsKeyword, SyntaxKind.NotContainsCsExpression },
                { SyntaxKind.NotBangContainsCsKeyword, SyntaxKind.NotContainsCsExpression },
                { SyntaxKind.StartsWithKeyword, SyntaxKind.StartsWithExpression },
                { SyntaxKind.NotStartsWithKeyword, SyntaxKind.NotStartsWithExpression },
                { SyntaxKind.StartsWithCsKeyword, SyntaxKind.StartsWithCsExpression },
                { SyntaxKind.NotStartsWithCsKeyword, SyntaxKind.NotStartsWithCsExpression },
                { SyntaxKind.EndsWithKeyword, SyntaxKind.EndsWithExpression },
                { SyntaxKind.NotEndsWithKeyword, SyntaxKind.NotEndsWithExpression },
                { SyntaxKind.EndsWithCsKeyword, SyntaxKind.EndsWithCsExpression },
                { SyntaxKind.NotEndsWithCsKeyword, SyntaxKind.NotEndsWithCsExpression },
                { SyntaxKind.MatchesRegexKeyword, SyntaxKind.MatchesRegexExpression }
            };

        private static bool IsTokenVisible(SyntaxKind tokenKind)
        {
            switch (tokenKind)
            {
                case SyntaxKind.LikeCsKeyword:
                case SyntaxKind.LikeKeyword:
                case SyntaxKind.NotLikeCsKeyword:
                case SyntaxKind.NotLikeKeyword:
                case SyntaxKind.ContainsCsKeyword:      // use contains_cs
                case SyntaxKind.NotContainsCsKeyword:   // use !contains_cs
                case SyntaxKind.NotContainsKeyword:     // use !contains
                    return false;
                default:
                    return true;
            }
        }

        private static string[] KnownQueryOperatorParameterNames = new string[] {
            "kind", "withsource", "with_source", "bagexpansion",
            "hint.concurrency", "hint.materialized", "hint.spread", "hint.remote", "hint.strategy", "hint.shufflekey", "hint.num_partitions",
            "expandoutput", "isfuzzy",
            "bin_legacy",
            "decodeblocks",
            "with_itemindex",
            "__packedColumn", "__noWithSource", "__sourceColumnIndex",
            "__crossDB", "__crossCluster", "__isFuzzy"
          };

        private static Parser<LexicalToken, NamedParameter> NamedParameter(Parser<LexicalToken, SyntaxToken> tokenParser, Parser<LexicalToken, Expression> valueParser, CompletionHint expressionHint = CompletionHint.None) =>
            Rule(
                AsIdentifierNameDeclaration(tokenParser),
                RequiredToken(SyntaxKind.EqualToken),
                valueParser,
                (name, equal, value) =>
                    new NamedParameter(name, equal, value, expressionHint));

        private static Parser<LexicalToken, NamedParameter> NamedParameter(Parser<LexicalToken, SyntaxToken> tokenParser, Parser<LexicalToken, Expression> valueParser, Func<Expression> missingValue, CompletionHint expressionHint = CompletionHint.None) =>
            Rule(
                AsIdentifierNameDeclaration(tokenParser),
                RequiredToken(SyntaxKind.EqualToken),
                Required(valueParser, missingValue ?? MissingValue),
                (name, equal, value) =>
                    new NamedParameter(name, equal, value, expressionHint));

        private static Parser<LexicalToken, NamedParameter> NamedParameter(SyntaxKind keyword, Parser<LexicalToken, Expression> valueParser, Func<Expression> missingValue = null, CompletionHint expressionHint = CompletionHint.None) =>
            NamedParameter(
                Token(keyword, ctext: SyntaxFacts.GetText(keyword) + "="),
                valueParser,
                missingValue,
                expressionHint);

        private static Parser<LexicalToken, NamedParameter> NamedParameter(string keyword, Parser<LexicalToken, Expression> valueParser, Func<Expression> missingValue = null, CompletionHint expressionHint = CompletionHint.None) =>
            NamedParameter(
                Token(keyword, ctext: keyword + "="),
                valueParser,
                missingValue,
                expressionHint);

        // keep this blank line separation before the #endregion to keep BRIDGE.Net from crashing
        #endregion
    }
}