module Dabbit.Transformations.StackAllocation

open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Stack allocation analysis
type AllocInfo = {
    Size: int option
    IsFixed: bool
    Source: string
}

/// Analyze and transform allocations to stack
module StackTransform =
    /// Identify allocation patterns
    let (|HeapAlloc|_|) = function
        | SynExpr.App(_, _, SynExpr.Ident ident, size, _) 
            when ident.idText = "Array.zeroCreate" || ident.idText = "Array.create" ->
            Some (size, ident.idText)
        | SynExpr.ArrayOrList(true, elements, _) ->
            Some (SynExpr.Const(SynConst.Int32 elements.Length, range.Zero), "array literal")
        | _ -> None
    
    /// Extract constant size
    let (|ConstSize|_|) = function
        | SynExpr.Const(SynConst.Int32 n, _) -> Some n
        | _ -> None
    
    /// Transform allocations to stack allocations
    let rec transform expr =
        match expr with
        | HeapAlloc(ConstSize size, source) when size <= 1024 ->
            // Transform to stack allocation
            let range = expr.Range
            SynExpr.App(
                ExprAtomicFlag.NonAtomic, false,
                SynExpr.App(
                    ExprAtomicFlag.NonAtomic, false,
                    SynExpr.Ident(Ident("NativePtr.stackalloc", range)),
                    SynExpr.Const(SynConst.Int32 size, range),
                    range),
                SynExpr.Ident(Ident("Span", range)),
                range)
        
        | SynExpr.App(flag, infix, func, arg, range) ->
            SynExpr.App(flag, infix, transform func, transform arg, range)
        
        | SynExpr.LetOrUse(isRec, isUse, bindings, body, range, trivia) ->
            let bindings' = bindings |> List.map (transformBinding)
            SynExpr.LetOrUse(isRec, isUse, bindings', transform body, range, trivia)
        
        | SynExpr.IfThenElse(cond, thenExpr, elseOpt, sp, isFromTry, range, trivia) ->
            SynExpr.IfThenElse(transform cond, transform thenExpr, 
                             Option.map transform elseOpt, sp, isFromTry, range, trivia)
        
        | SynExpr.Match(sp, matchExpr, clauses, range, trivia) ->
            let clauses' = clauses |> List.map (fun (SynMatchClause(pat, when', result, r, sp, tr)) ->
                SynMatchClause(pat, Option.map transform when', transform result, r, sp, tr))
            SynExpr.Match(sp, transform matchExpr, clauses', range, trivia)
        
        | SynExpr.Sequential(sp, isTrueSeq, e1, e2, range, trivia) ->
            SynExpr.Sequential(sp, isTrueSeq, transform e1, transform e2, range, trivia)
        
        | _ -> expr
    
    and transformBinding (SynBinding(access, kind, isInline, isMut, attrs, xmlDoc, 
                                   valData, headPat, retInfo, expr, range, sp, trivia)) =
        SynBinding(access, kind, isInline, isMut, attrs, xmlDoc, valData,
                   headPat, retInfo, transform expr, range, sp, trivia)

/// Verify stack safety
module StackSafety =
    /// Check if expression only uses stack allocations
    let rec verify expr =
        match expr with
        | SynExpr.App(_, _, SynExpr.Ident ident, _, _) ->
            match ident.idText with
            | "Array.zeroCreate" | "Array.create" | "List.init" -> 
                Error "Heap allocation detected"
            | "NativePtr.stackalloc" | "Span" -> 
                Ok ()
            | _ -> Ok ()
        
        | SynExpr.ArrayOrList(_, _, _) ->
            Error "Array/List literal creates heap allocation"
        
        | SynExpr.App(_, _, func, arg, _) ->
            Result.bind (fun _ -> verify arg) (verify func)
        
        | SynExpr.LetOrUse(_, _, bindings, body, _, _) ->
            let bindResults = bindings |> List.map verifyBinding
            match List.tryFind Result.isError bindResults with
            | Some err -> err
            | None -> verify body
        
        | SynExpr.Sequential(_, _, e1, e2, _, _) ->
            Result.bind (fun _ -> verify e2) (verify e1)
        
        | _ -> Ok ()
    
    and verifyBinding (SynBinding(_, _, _, _, _, _, _, _, _, expr, _, _, _)) =
        verify expr