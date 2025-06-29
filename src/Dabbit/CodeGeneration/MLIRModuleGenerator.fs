module Dabbit.CodeGeneration.MLIRModuleGenerator

open System
open System.Text
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open Core.Types.TypeSystem
open Core.XParsec.Foundation
open MLIREmitter
open MLIRBuiltins
open MLIRExpressionGenerator
open MLIRTypeOperations
open MLIRControlFlow

/// Utility functions
let lift value = mlir { return value }

let rec mapM (f: 'a -> MLIRCombinator<'b>) (list: 'a list): MLIRCombinator<'b list> =
    mlir {
        match list with
        | [] -> return []
        | head :: tail ->
            let! mappedHead = f head
            let! mappedTail = mapM f tail
            return mappedHead :: mappedTail
    }

/// Module-level constructs using Foundation patterns
module rec ModuleConstruction =
    
    /// Generate top-level module
    let generateModule (moduleName: string) (declarations: SynModuleDecl list): MLIRCombinator<unit> =
        mlir {
            do! Blocks.moduleDef moduleName (mlir {
                // Emit format strings first
                do! FormatStrings.emitFormatStrings
                do! Core.emitBlankLine
                
                // Process all declarations
                for decl in declarations do
                    do! generateModuleDeclaration decl
                    do! Core.emitBlankLine
                
                // Emit external declarations at the end
                do! Externals.emitExternalDeclarations
            })
        }
    
    /// Generate individual module declarations
    let generateModuleDeclaration (decl: SynModuleDecl): MLIRCombinator<unit> =
        mlir {
            match decl with
            | SynModuleDecl.Let(_, bindings, _) ->
                for binding in bindings do
                    do! FunctionGeneration.generateTopLevelBinding binding
                    
            | SynModuleDecl.Expr(_, expr, _) ->
                do! DoExpressions.generateDoExpression expr
                
            | SynModuleDecl.Types(typeDefs, _) ->
                for typeDef in typeDefs do
                    do! TypeGeneration.generateTypeDefinition typeDef
                    
            | SynModuleDecl.Open(target, _) ->
                do! NamespaceGeneration.generateOpenDeclaration target
                
            | SynModuleDecl.ModuleAbbrev(ident, longIdent, _) ->
                do! NamespaceGeneration.generateModuleAbbreviation ident longIdent
                
            | SynModuleDecl.NestedModule(componentInfo, _, nestedDecls, _, _, _) ->
                let (SynComponentInfo(_, _, _, longIdent, _, _, _, _)) = componentInfo
                let moduleName = longIdent |> List.map (fun id -> id.idText) |> String.concat "_"
                do! NamespaceGeneration.generateNestedModule moduleName nestedDecls
                
            | SynModuleDecl.Exception(exnDef, _) ->
                do! ExceptionGeneration.generateExceptionDefinition exnDef
                
            | _ ->
                do! Core.emitComment (sprintf "Unsupported module declaration: %A" decl)
        }

/// Function definitions using Foundation combinators
module rec FunctionGeneration =
    
    /// Generate top-level function binding
    let generateTopLevelBinding (binding: SynBinding): MLIRCombinator<unit> =
        mlir {
            let (SynBinding(_, kind, _, _, _, _, valData, headPat, returnInfo, expr, _, _, _)) = binding
            match headPat with
            | SynPat.LongIdent(SynLongIdent([ident], _, _), _, _, argPats, _, _) ->
                // Function definition
                return! generateFunctionDefinition ident argPats returnInfo expr (kind = SynBindingKind.Inline)
                
            | SynPat.Named(SynIdent(ident, _), _, _, _) ->
                // Value binding
                return! generateValueBinding ident expr
                
            | _ ->
                do! Core.emitComment "Complex pattern binding not fully supported"
        }
    
    /// Generate function definition with parameters
    let generateFunctionDefinition (ident: Ident) (argPats: SynArgPats) (returnInfo: SynBindingReturnInfo option) 
                                   (bodyExpr: SynExpr) (isInline: bool): MLIRCombinator<unit> =
        mlir {
            let funcName = ident.idText
            
            // Extract parameter information
            let! parameters = extractParameters argPats
            
            // Determine return type
            let! returnType = determineReturnType returnInfo bodyExpr
            
            // Generate function
            do! Blocks.functionDef funcName parameters returnType (mlir {
                let! bodyResult = Core.generateExpression bodyExpr
                
                // Ensure return type compatibility
                let! finalResult = 
                    if TypeAnalysis.areEqual bodyResult.Type returnType then
                        lift bodyResult
                    else
                        Conversions.implicitConvert bodyResult.Type returnType bodyResult
                
                do! Functions.return' (Some finalResult)
                return ()
            })
            
            if isInline then
                do! Core.emitComment (sprintf "Function %s marked as inline" funcName)
        }
    
    /// Extract parameter information from argument patterns
    let extractParameters (argPats: SynArgPats): MLIRCombinator<(string * MLIRType) list> =
        mlir {
            match argPats with
            | SynArgPats.Pats(patterns) ->
                return! mapM extractParameterFromPattern patterns
                
            | SynArgPats.NamePatPairs(pairs, _) ->
                let extractPair (pair: SynIdent * _ * SynPat) =
                    let (_, _, pat) = pair
                    extractParameterFromPattern pat
                return! mapM extractPair pairs
        }
    
    /// Extract parameter from pattern
    let extractParameterFromPattern (pattern: SynPat): MLIRCombinator<string * MLIRType> =
        mlir {
            match pattern with
            | SynPat.Named(SynIdent(ident, _), _, _, _) ->
                // Default to i32 for now - would need type inference
                return (ident.idText, MLIRTypes.i32)
                
            | SynPat.Typed(SynPat.Named(SynIdent(ident, _), _, _, _), synType, _) ->
                let! mlirType = Mapping.synTypeToMLIR synType
                return (ident.idText, mlirType)
                
            | SynPat.Paren(innerPat, _) ->
                return! extractParameterFromPattern innerPat
                
            | _ ->
                return! fail "parameter_extraction" "Unsupported parameter pattern"
        }
    
    /// Determine function return type
    let determineReturnType (returnInfo: SynBindingReturnInfo option) (bodyExpr: SynExpr): MLIRCombinator<MLIRType> =
        mlir {
            match returnInfo with
            | Some (SynBindingReturnInfo(synType, _, _)) ->
                return! Mapping.synTypeToMLIR synType
            | None ->
                // Infer from body expression - simplified
                return MLIRTypes.i32  // Default for now
        }
    
    /// Generate simple value binding
    let generateValueBinding (ident: Ident) (expr: SynExpr): MLIRCombinator<unit> =
        mlir {
            let! value = Core.generateExpression expr
            
            // Create global constant
            let globalName = sprintf "@%s" ident.idText
            let typeStr = Core.formatType value.Type
            
            do! emitLine (sprintf "llvm.mlir.global internal @%s(%s) : %s" ident.idText value.SSA typeStr)
            do! bindLocal ident.idText globalName typeStr
        }

/// Type definitions using Foundation patterns
module rec TypeGeneration =
    
    /// Generate type definition
    let generateTypeDefinition (typeDef: SynTypeDefn): MLIRCombinator<unit> =
        mlir {
            let (SynTypeDefn(componentInfo, repr, _, _, _, _)) = typeDef
            let (SynComponentInfo(_, _, _, longIdent, _, _, _, _)) = componentInfo
            let typeName = longIdent |> List.map (fun id -> id.idText) |> String.concat "_"
            
            match repr with
            | SynTypeDefnRepr.Simple(simpleRepr, _) ->
                do! generateSimpleTypeRepr typeName simpleRepr
                
            | SynTypeDefnRepr.ObjectModel(_, members, _) ->
                do! generateObjectModel typeName members
                
            | _ ->
                do! Core.emitComment (sprintf "Unsupported type representation for %s" typeName)
        }
    
    /// Generate simple type representations
    let generateSimpleTypeRepr (typeName: string) (simpleRepr: SynTypeDefnSimpleRepr): MLIRCombinator<unit> =
        mlir {
            match simpleRepr with
            | SynTypeDefnSimpleRepr.Record(_, fields, _) ->
                do! generateRecordType typeName fields
                
            | SynTypeDefnSimpleRepr.Union(_, cases, _) ->
                do! generateUnionType typeName cases
                
            | SynTypeDefnSimpleRepr.TypeAbbrev(_, synType, _) ->
                do! generateTypeAlias typeName synType
                
            | _ ->
                do! Core.emitComment (sprintf "Unsupported simple type representation for %s" typeName)
        }
    
    /// Generate record type definition
    let generateRecordType (typeName: string) (fields: SynField list): MLIRCombinator<unit> =
        mlir {
            do! Core.emitComment (sprintf "Record type: %s" typeName)
            
            let extractField (field: SynField) =
                let (SynField(_, _, fieldIdOpt, fieldType, _, _, _, _, _)) = field
                let fieldName = 
                    match fieldIdOpt with
                    | Some id -> id.idText
                    | None -> "anonymous"
                
                mlir {
                    let! mlirType = Mapping.synTypeToMLIR fieldType
                    return (fieldName, mlirType)
                }
                
            let! fieldTypes = mapM extractField fields
            
            let! structType = StructTypes.createStructType fieldTypes
            do! Core.emitComment (sprintf "Struct type %s: %s" typeName (Core.formatType structType))
        }
    
    /// Generate discriminated union type
    let generateUnionType (typeName: string) (cases: SynUnionCase list): MLIRCombinator<unit> =
        mlir {
            do! Core.emitComment (sprintf "Union type: %s" typeName)
            
            let extractCase (case: SynUnionCase) =
                let (SynUnionCase(_, SynIdent(ident, _), caseType, _, _, _, _)) = case
                let caseName = ident.idText
                
                mlir {
                    match caseType with
                    | SynUnionCaseKind.Fields(fields) ->
                        let extractFieldType (field: SynField) =
                            let (SynField(_, _, _, fieldType, _, _, _, _, _)) = field
                            Mapping.synTypeToMLIR fieldType
                        let! fieldTypes = mapM extractFieldType fields
                        return (caseName, fieldTypes)
                        
                    | SynUnionCaseKind.FullType(synType, _) ->
                        let! caseType = Mapping.synTypeToMLIR synType
                        return (caseName, [caseType])
                }
                
            let! caseTypes = mapM extractCase cases
            
            let! unionType = UnionTypes.createUnionType caseTypes
            do! Core.emitComment (sprintf "Union type %s: %s" typeName (Core.formatType unionType))
        }
    
    /// Generate type alias
    let generateTypeAlias (typeName: string) (synType: SynType): MLIRCombinator<unit> =
        mlir {
            let! mlirType = Mapping.synTypeToMLIR synType
            do! Core.emitComment (sprintf "Type alias %s = %s" typeName (Core.formatType mlirType))
        }
    
    /// Generate object model (classes, interfaces)
    let generateObjectModel (typeName: string) (members: SynMemberDefn list): MLIRCombinator<unit> =
        mlir {
            do! Core.emitComment (sprintf "Object model for %s not fully supported" typeName)
            
            for memberDef in members do
                do! generateMemberDefinition typeName memberDef
        }
    
    /// Generate member definition
    let generateMemberDefinition (typeName: string) (memberDef: SynMemberDefn): MLIRCombinator<unit> =
        mlir {
            match memberDef with
            | SynMemberDefn.Member(binding, _) ->
                do! FunctionGeneration.generateTopLevelBinding binding
                
            | SynMemberDefn.ImplicitCtor(_, _, ctorArgs, _, _, _) ->
                do! Core.emitComment (sprintf "Constructor for %s" typeName)
                
            | _ ->
                do! Core.emitComment "Unsupported member definition"
        }

/// Exception definitions
module ExceptionGeneration =
    
    /// Generate exception definition
    let generateExceptionDefinition (exnDef: SynExceptionDefn): MLIRCombinator<unit> =
        mlir {
            let (SynExceptionDefn(exnRepr, _, _)) = exnDef
            let (SynExceptionDefnRepr(_, unionCase, _, _, _, _)) = exnRepr
            let (SynUnionCase(_, SynIdent(ident, _), caseType, _, _, _, _)) = unionCase
            let exnName = ident.idText
            do! Core.emitComment (sprintf "Exception definition: %s" exnName)
            
            // Exceptions represented as tagged unions
            match caseType with
            | SynUnionCaseKind.Fields(fields) ->
                let extractFieldType (field: SynField) =
                    let (SynField(_, _, _, fieldType, _, _, _, _, _)) = field
                    Mapping.synTypeToMLIR fieldType
                let! fieldTypes = mapM extractFieldType fields
                
                let! exnType = UnionTypes.createUnionType [(exnName, fieldTypes)]
                do! Core.emitComment (sprintf "Exception type: %s" (Core.formatType exnType))
                
            | _ ->
                do! Core.emitComment "Simple exception (no data)"
        }

/// Namespace and module organization
module NamespaceGeneration =
    
    /// Generate open declaration
    let generateOpenDeclaration (target: SynOpenDeclTarget): MLIRCombinator<unit> =
        mlir {
            match target with
            | SynOpenDeclTarget.ModuleOrNamespace(SynLongIdent(ids, _, _), _) ->
                let namespacePath = ids |> List.map (fun id -> id.idText) |> String.concat "."
                do! Core.emitComment (sprintf "Open namespace: %s" namespacePath)
                
                // Update namespace context
                do! updateMLIRState (fun s -> 
                    { s with Firefly = { s.Firefly with ImportedModules = namespacePath :: s.Firefly.ImportedModules } })
                
            | _ ->
                do! Core.emitComment "Unsupported open declaration target"
        }
    
    /// Generate module abbreviation
    let generateModuleAbbreviation (ident: Ident) (longIdent: SynLongIdent): MLIRCombinator<unit> =
        mlir {
            let (SynLongIdent(ids, _, _)) = longIdent
            let fullName = ids |> List.map (fun id -> id.idText) |> String.concat "."
            do! Core.emitComment (sprintf "Module abbreviation: %s = %s" ident.idText fullName)
        }
    
    /// Generate nested module
    let generateNestedModule (moduleName: string) (declarations: SynModuleDecl list): MLIRCombinator<unit> =
        mlir {
            do! Core.emitComment (sprintf "Nested module: %s" moduleName)
            
            for decl in declarations do
                do! ModuleConstruction.generateModuleDeclaration decl
        }

/// Top-level do expressions
module DoExpressions =
    
    /// Generate do expression (module initialization)
    let generateDoExpression (expr: SynExpr): MLIRCombinator<unit> =
        mlir {
            do! Core.emitComment "Module initialization expression"
            
            // Generate initialization function
            do! Blocks.functionDef "__module_init" [] MLIRTypes.void_ (mlir {
                let! result = Core.generateExpression expr
                do! Functions.return' None
                return ()
            })
        }

/// Program entry points
module EntryPoints =
    
    /// Generate main function entry point
    let generateMainFunction (programName: string) (mainExpr: SynExpr option): MLIRCombinator<unit> =
        mlir {
            match mainExpr with
            | Some expr ->
                do! Blocks.functionDef "main" [] MLIRTypes.i32 (mlir {
                    let! result = Core.generateExpression expr
                    
                    // Ensure main returns i32
                    let! exitCode = 
                        if TypeAnalysis.areEqual result.Type MLIRTypes.i32 then
                            lift result
                        else
                            Constants.intConstant 0 32
                    
                    do! Functions.return' (Some exitCode)
                    return ()
                })
                
            | None ->
                do! Blocks.functionDef "main" [] MLIRTypes.i32 (mlir {
                    let! exitCode = Constants.intConstant 0 32
                    do! Functions.return' (Some exitCode)
                    return ()
                })
        }

/// Complete program generation orchestration
module rec ProgramGeneration =
    
    /// Generate complete MLIR program from F# parsed input
    let generateProgram (programName: string) (parsedInputs: (string * ParsedInput) list): MLIRCombinator<string> =
        mlir {
            do! Core.emitComment (sprintf "Generated MLIR for program: %s" programName)
            do! Core.emitComment (sprintf "Timestamp: %s" (DateTime.Now.ToString()))
            do! Core.emitBlankLine
            
            let! result = Blocks.moduleDef programName (mlir {
                // Process all input files
                for (fileName, parsedInput) in parsedInputs do
                    do! Core.emitComment (sprintf "Processing file: %s" fileName)
                    do! processInputFile parsedInput
                    do! Core.emitBlankLine
                
                // Generate main function if needed
                do! EntryPoints.generateMainFunction programName None
            })
            
            // Extract generated MLIR text
            let! state = getMLIRState
            return state.MLIR.Output.ToString()
        }
    
    /// Process individual input file
    let processInputFile (parsedInput: ParsedInput): MLIRCombinator<unit> =
        mlir {
            match parsedInput with
            | ParsedInput.ImplFile(implFile) ->
                let (ParsedImplFileInput(fileName, _, _, _, _, modules, _)) = implFile
                for moduleOrNamespace in modules do
                    let (SynModuleOrNamespace(longIdent, _, kind, declarations, _, _, _, _, _)) = moduleOrNamespace
                    let moduleName = longIdent |> List.map (fun id -> id.idText) |> String.concat "_"
                    
                    match kind with
                    | SynModuleOrNamespaceKind.NamedModule ->
                        do! NamespaceGeneration.generateNestedModule moduleName declarations
                    | SynModuleOrNamespaceKind.AnonModule ->
                        for decl in declarations do
                            do! ModuleConstruction.generateModuleDeclaration decl
                    | SynModuleOrNamespaceKind.DeclaredNamespace ->
                        do! Core.emitComment (sprintf "Namespace: %s" moduleName)
                        for decl in declarations do
                            do! ModuleConstruction.generateModuleDeclaration decl
                    | SynModuleOrNamespaceKind.GlobalNamespace ->
                        for decl in declarations do
                            do! ModuleConstruction.generateModuleDeclaration decl
                            
            | ParsedInput.SigFile(_) ->
                do! Core.emitComment "Signature files not processed in code generation"
        }

/// Utility functions for program generation
module Utilities =
    
    /// Create initial program generation state
    let createInitialState (programName: string): MLIRBuilderState =
        initialMLIRBuilderState programName
    
    /// Run complete program generation
    let runProgramGeneration (programName: string) (parsedInputs: (string * ParsedInput) list): Result<string, string list> =
        let initialState = createInitialState programName
        let combinator = ProgramGeneration.generateProgram programName parsedInputs
        
        match runMLIRCombinator combinator initialState with
        | Success(output, state) -> Ok output
        | Failure errors -> Error (errors |> List.map (fun e -> e.ToString()))
    
    /// Validate generated MLIR (basic syntax check)
    let validateMLIR (mlirCode: string): Result<unit, string list> =
        // Basic validation - check for balanced braces
        let braceBalance = 
            mlirCode.ToCharArray()
            |> Array.fold (fun acc c ->
                match c with
                | '{' -> acc + 1
                | '}' -> acc - 1
                | _ -> acc) 0
        
        if braceBalance = 0 then
            Ok ()
        else
            Error [sprintf "Unbalanced braces in generated MLIR (balance: %d)" braceBalance]
    
    /// Pretty print MLIR with syntax highlighting comments
    let prettyPrintMLIR (mlirCode: string): string =
        let lines = mlirCode.Split('\n')
        let mutable indentLevel = 0
        let result = StringBuilder()
        
        for line in lines do
            let trimmed = line.Trim()
            
            if trimmed.EndsWith("{") then
                result.AppendLine(sprintf "%s%s" (String.replicate indentLevel "  ") trimmed) |> ignore
                indentLevel <- indentLevel + 1
            elif trimmed = "}" then
                indentLevel <- max 0 (indentLevel - 1)
                result.AppendLine(sprintf "%s%s" (String.replicate indentLevel "  ") trimmed) |> ignore
            elif not (String.IsNullOrWhiteSpace(trimmed)) then
                result.AppendLine(sprintf "%s%s" (String.replicate indentLevel "  ") trimmed) |> ignore
            else
                result.AppendLine() |> ignore
        
        result.ToString()