using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ProtoCore.Exceptions;
using ProtoCore.Lang;
using ProtoFFI;
using ProtoCore.Utils;
using ProtoCore.Runtime;

namespace ProtoCore.DSASM.Mirror
{
    public class SymbolNotFoundException: Exception
    {
        public SymbolNotFoundException(string symbolName)
            : base("Cannot find symbol: " + symbolName)
        {
            this.SymbolName = symbolName;
        }

        public string SymbolName { get; private set; } 
    }

    //Status: Draft, experiment
    
    /// <summary>
    /// Provides reflective capabilities over the execution of a DSASM Executable
    /// </summary>
    public class ExecutionMirror
    {
        private readonly ProtoCore.RuntimeCore runtimeCore;
        public Executive MirrorTarget { get; private set; }
        private OutputFormatParameters formatParams;
        private Dictionary<string, List<string>> propertyFilter;

        /// <summary>
        /// Create a mirror for a given executive
        /// </summary>
        /// <param name="exec"></param>
        public ExecutionMirror(ProtoCore.DSASM.Executive exec, ProtoCore.RuntimeCore coreObj)
        {
            Validity.Assert(exec != null, "Can't mirror a null executive");

            runtimeCore = coreObj;
            MirrorTarget = exec;

            LoadPropertyFilters();
        }

        private void LoadPropertyFilters()
        {
            if (runtimeCore.Options.RootCustomPropertyFilterPathName == null)
            {
                return;
            }

            System.IO.FileInfo file = new System.IO.FileInfo(runtimeCore.Options.RootCustomPropertyFilterPathName);
            if (!file.Exists)
            {
                return;
            }

            using (var stream = file.OpenText())
            {
                propertyFilter = new Dictionary<string, List<string>>();
                var line = stream.ReadLine();
                while (line != null)
                {
                    //  after removing leading and trailing spaces if there is something then
                    //  only try to tokenize
                    //
                    line = line.Trim();
                    if (line.Length != 0)
                    {
                        if (line.StartsWith(";"))
                        {
                            //  now over to next line
                            line = stream.ReadLine();

                            //  neglect a comment;
                            continue;
                        }
                        //  Point X,Y,Z
                        //
                        //  this will give you two strings:
                        //  0-> Point
                        //  1-> X,Y,Z
                        var splitStrings1 = line.Split(' ', ',');

                        //  first string in this array is class-name
                        //
                        var className = splitStrings1[0];

                        //  second string is optional, so check it it exists
                        //
                        List<string> classProps = null;
                        if (splitStrings1.Length > 1)
                        {
                            classProps = new List<string>();
                            for (int i = 1; i < splitStrings1.Length; ++i)
                            {
                                while (String.IsNullOrWhiteSpace(splitStrings1[i]))
                                {
                                    i++;
                                }
                                classProps.Add(splitStrings1[i]);
                            }
                        }
                        propertyFilter.Add(className, classProps);
                    }

                    //  now over to next line
                    line = stream.ReadLine();
                }
            }
        }

        public string PrintClass(StackValue val, Heap heap, int langblock, bool forPrint)
        {
            return PrintClass(val, heap, langblock, -1, -1, forPrint);
        }

        public string PrintClass(StackValue val, Heap heap, int langblock, int maxArraySize, int maxOutputDepth, bool forPrint)
        {
            if (null == formatParams)
                formatParams = new OutputFormatParameters(maxArraySize, maxOutputDepth);

            return GetClassTrace(val, heap, langblock, forPrint);
        }

        private string GetFormattedValue(string varname, string value)
        {
            return string.Format("{0} = {1}", varname, value);
        }

        public string GetStringValue(StackValue val, Heap heap, int langblock, bool forPrint = false)
        {
            return GetStringValue(val, heap, langblock, -1, -1, forPrint);
        }

        public string GetStringValue(StackValue val, Heap heap, int langblock, int maxArraySize, int maxOutputDepth, bool forPrint = false)
        {
            if (formatParams == null)
                formatParams = new OutputFormatParameters(maxArraySize, maxOutputDepth);

            switch (val.optype)
            {
                case AddressType.Int:
                    return val.opdata.ToString();
                case AddressType.Double:
                    return val.RawDoubleValue.ToString("F6");
                case AddressType.Null:
                    return "null";
                case AddressType.Pointer:
                    return GetClassTrace(val, heap, langblock, forPrint);
                case AddressType.ArrayPointer:
                    HashSet<int> pointers = new HashSet<int>{(int)val.opdata};
                    string arrTrace = GetArrayTrace(val, heap, langblock, pointers, forPrint);
                    if (forPrint)
                        return "{" + arrTrace + "}";
                    else
                        return "{ " + arrTrace + " }";
                case AddressType.FunctionPointer:
                    ProcedureNode procNode;
                    if (runtimeCore.DSExecutable.FuncPointerTable.TryGetFunction(val, runtimeCore, out procNode))
                    {
                        string className = String.Empty;
                        if (procNode.ClassID != Constants.kGlobalScope)
                        {
                            className = runtimeCore.DSExecutable.classTable.GetTypeName(procNode.ClassID).Split('.').Last() + ".";
                        }

                        return "function: " + className + procNode.Name; 
                    }
                    return "function: " + val.opdata.ToString();

                case AddressType.Boolean:
                    return (val.opdata == 0) ? "false" : "true";
                case AddressType.String:
                    if (forPrint)
                        return heap.ToHeapObject<DSString>(val).Value;
                    else
                        return "\"" + heap.ToHeapObject<DSString>(val).Value + "\"";                    
                case AddressType.Char:
                    Char character = Convert.ToChar(val.opdata); 
                    if (forPrint)
                        return character.ToString();
                    else
                        return "'" + character + "'";
                default:
                    return "null"; // "Value not yet supported for tracing";
            }
        }

        public string GetClassTrace(StackValue val, Heap heap, int langblock, bool forPrint)
        {
            if (!formatParams.ContinueOutputTrace())
                return "...";

            RuntimeMemory rmem = MirrorTarget.rmem;
            Executable exe = MirrorTarget.exe;
            ClassTable classTable = MirrorTarget.RuntimeCore.DSExecutable.classTable;

            int classtype = val.metaData.type;
            if (classtype < 0 || (classtype >= classTable.ClassNodes.Count))
            {
                formatParams.RestoreOutputTraceDepth();
                return string.Empty;
            }

            ClassNode classnode = classTable.ClassNodes[classtype];
            if (classnode.IsImportedClass)
            {
                var helper = DLLFFIHandler.GetModuleHelper(FFILanguage.CSharp);
                var marshaller = helper.GetMarshaller(runtimeCore);
                var strRep = marshaller.GetStringValue(val);
                formatParams.RestoreOutputTraceDepth();
                return strRep;
            }
            else
            {
                var obj = heap.ToHeapObject<DSObject>(val);

                List<string> visibleProperties = null;
                if (null != propertyFilter)
                {
                    if (!propertyFilter.TryGetValue(classnode.Name, out visibleProperties))
                        visibleProperties = null;
                }

                StringBuilder classtrace = new StringBuilder();
                if (classnode.Symbols != null && classnode.Symbols.symbolList.Count > 0)
                {
                    bool firstPropertyDisplayed = false;
                    for (int n = 0; n < obj.Count; ++n)
                    {
                        SymbolNode symbol = classnode.Symbols.symbolList[n];
                        string propName = symbol.name;

                        if ((null != visibleProperties) && visibleProperties.Contains(propName) == false)
                            continue; // This property is not to be displayed.

                        if (firstPropertyDisplayed)
                            classtrace.Append(", ");

                        string propValue = "";
                        if (symbol.isStatic)
                        {
                            var staticSymbol = exe.runtimeSymbols[langblock].symbolList[symbol.symbolTableIndex];
                            StackValue staticProp = rmem.GetSymbolValue(staticSymbol);
                            propValue = GetStringValue(staticProp, heap, langblock, forPrint);
                        }
                        else
                        {
                            propValue = GetStringValue(obj.GetValueFromIndex(symbol.index, runtimeCore), heap, langblock, forPrint);
                        }
                        classtrace.Append(string.Format("{0} = {1}", propName, propValue));
                        firstPropertyDisplayed = true;
                    }
                }
                else
                {
                    var stringValues = obj.Values.Select(x => GetStringValue(x, heap, langblock, forPrint))
                                                      .ToList();

                    for (int n = 0; n < stringValues.Count(); ++n)
                    {
                        if (0 != n)
                            classtrace.Append(", ");

                        classtrace.Append(stringValues[n]);
                    }
                }

                formatParams.RestoreOutputTraceDepth();
                if (classtype >= (int)ProtoCore.PrimitiveType.kMaxPrimitives)
                    if (forPrint)
                        return (string.Format("{0}{{{1}}}", classnode.Name, classtrace.ToString()));
                    else
                    {
                        string tempstr =  (string.Format("{0}({1})", classnode.Name, classtrace.ToString()));
                        return tempstr;
                    }

                return classtrace.ToString();
            }
        }

        private string GetPointerTrace(StackValue ptr, Heap heap, int langblock, HashSet<int> pointers, bool forPrint)
        {
            if (pointers.Contains((int)ptr.opdata))
            {
                return "{ ... }";
            }
            else
            {
                pointers.Add((int)ptr.opdata);

                if (forPrint)
                {
                    return "{" + GetArrayTrace(ptr, heap, langblock, pointers, forPrint) + "}";
                }
                else
                {
                    return "{ " + GetArrayTrace(ptr, heap, langblock, pointers, forPrint) + " }";
                }
            }
        }

        private string GetArrayTrace(StackValue svArray, Heap heap, int langblock, HashSet<int> pointers, bool forPrint)
        {
            if (!formatParams.ContinueOutputTrace())
                return "...";

            StringBuilder arrayElements = new StringBuilder();
            var array = heap.ToHeapObject<DSArray>(svArray);

            int halfArraySize = -1;
            if (formatParams.MaxArraySize > 0) // If the caller did specify a max value...
            {
                // And our array is larger than that max value...
                if (array.Count > formatParams.MaxArraySize)
                    halfArraySize = (int)Math.Floor(formatParams.MaxArraySize * 0.5);
            }

            int totalElementCount = array.Count; 
            if (svArray.IsArray)
            {
                totalElementCount = heap.ToHeapObject<DSArray>(svArray).Values.Count();
            }

            for (int n = 0; n < array.Count; ++n)
            {
                // As we try to output the next element in the array, there 
                // should be a comma if there were previously output element.
                if (arrayElements.Length > 0)
                    if(forPrint)
                        arrayElements.Append(",");
                    else
                        arrayElements.Append(", ");

                StackValue sv = array.GetValueFromIndex(n, runtimeCore);
                if (sv.IsArray)
                {
                    arrayElements.Append(GetPointerTrace(sv, heap, langblock, pointers, forPrint));
                }
                else
                {
                    arrayElements.Append(GetStringValue(array.GetValueFromIndex(n, runtimeCore), heap, langblock, forPrint));
                }

                // If we need to truncate this array (halfArraySize > 0), and we have 
                // already reached the first half of it, then offset the loop counter 
                // to the next half of the array.
                if (halfArraySize > 0 && (n == halfArraySize - 1))
                {
                    arrayElements.Append(", ...");
                    n = totalElementCount - halfArraySize - 1;
                }
            }

            if (svArray.IsArray)
            {
                var dict = array.ToDictionary().Where(kvp => !kvp.Key.IsInteger);

                int startIndex = (halfArraySize > 0) ? dict.Count() - halfArraySize : 0;
                int index = -1;

                foreach (var keyValuePair in dict)
                {
                    index++;
                    if (index < startIndex)
                    {
                        continue;
                    }

                    if (arrayElements.Length > 0)
                    {
                        if (forPrint)
                        {
                            arrayElements.Append(",");
                        }
                        else
                        {
                            arrayElements.Append(", ");
                        }
                    }

                    StackValue key = keyValuePair.Key;
                    StackValue value = keyValuePair.Value;

                    if (key.IsArray)
                    {
                        arrayElements.Append(GetPointerTrace(key, heap, langblock, pointers, forPrint));
                    }
                    else
                    {
                        arrayElements.Append(GetStringValue(key, heap, langblock, forPrint));
                    }

                    arrayElements.Append("=");

                    if (value.IsArray)
                    {
                        arrayElements.Append(GetPointerTrace(value, heap, langblock, pointers, forPrint));
                    }
                    else
                    {
                        arrayElements.Append(GetStringValue(value, heap, langblock, forPrint));
                    }
                }
            }

            formatParams.RestoreOutputTraceDepth();
            return arrayElements.ToString();
        }

        private string GetGlobalVarTrace(List<string> variableTraces)
        {
            // Prints out the final Value of every symbol in the program
            // Traverse order:
            //  Exelist, Globals symbols

            StringBuilder globaltrace = null;
            if (null == variableTraces)
                globaltrace = new StringBuilder();

            ProtoCore.DSASM.Executable exe = MirrorTarget.exe;

            // Only display symbols defined in the default top-most langauge block;
            // Otherwise garbage information may be displayed.
            if (exe.runtimeSymbols.Length > 0)
            {
                int blockId = 0;

                // when this code block is of type construct, such as if, else, while, all the symbols inside are local
                //if (exe.instrStreamList[blockId] == null) 
                //    continue;

                SymbolTable symbolTable = exe.runtimeSymbols[blockId];
                for (int i = 0; i < symbolTable.symbolList.Count; ++i)
                {
                    formatParams.ResetOutputDepth();
                    SymbolNode symbolNode = symbolTable.symbolList[i];

                    bool isLocal = Constants.kGlobalScope != symbolNode.functionIndex;
                    bool isStatic = (symbolNode.classScope != Constants.kInvalidIndex && symbolNode.isStatic);
                    if (symbolNode.isArgument || isLocal || isStatic || symbolNode.isTemp)
                    {
                        // These have gone out of scope, their values no longer exist
                        continue;
                    }

                    RuntimeMemory rmem = MirrorTarget.rmem;
                    StackValue sv = rmem.GetSymbolValue(symbolNode);
                    string formattedString = GetFormattedValue(symbolNode.name, GetStringValue(sv, rmem.Heap, blockId));

                    if (null != globaltrace)
                    {
                        int maxLength = 1020;
                        while (formattedString.Length > maxLength)
                        {
                            globaltrace.AppendLine(formattedString.Substring(0, maxLength));
                            formattedString = formattedString.Remove(0, maxLength);
                        }

                        globaltrace.AppendLine(formattedString);
                    }

                    if (null != variableTraces)
                        variableTraces.Add(formattedString);
                }

                formatParams.ResetOutputDepth();
            }

            return ((null == globaltrace) ? string.Empty : globaltrace.ToString());
        }

        public string GetCoreDump()
        {
            formatParams = new OutputFormatParameters();
            return GetGlobalVarTrace(null);
        }

        public void GetCoreDump(out List<string> variableTraces, int maxArraySize, int maxOutputDepth)
        {
            variableTraces = new List<string>();
            formatParams = new OutputFormatParameters(maxArraySize, maxOutputDepth);
            GetGlobalVarTrace(variableTraces);
        }

        private int GetSymbolIndex(string name, out int ci, ref int block, out SymbolNode symbol)
        {
            RuntimeMemory rmem = MirrorTarget.rmem;
            ProtoCore.DSASM.Executable exe = runtimeCore.DSExecutable;

            int functionIndex = Constants.kGlobalScope;
            ci = Constants.kInvalidIndex;
            int functionBlock = Constants.kGlobalScope;

            if (runtimeCore.DebugProps.DebugStackFrameContains(DebugProperties.StackFrameFlagOptions.FepRun))
            {
                ci = runtimeCore.watchClassScope = rmem.CurrentStackFrame.ClassScope;
                functionIndex = rmem.CurrentStackFrame.FunctionScope;
                functionBlock = rmem.CurrentStackFrame.FunctionBlock;
            }

            // TODO Jun: 'block' is incremented only if there was no other block provided by the programmer
            // This is only to address NUnit issues when retrieving a global variable
            // Some predefined functions are hard coded in the AST so isSingleAssocBlock will never be true
            //if (exe.isSingleAssocBlock)
            //{
            //    ++block;
            //}

            int index = -1;
            if (ci != Constants.kInvalidIndex)
            {
                ClassNode classnode = runtimeCore.DSExecutable.classTable.ClassNodes[ci];

                if (functionIndex != ProtoCore.DSASM.Constants.kInvalidIndex && functionBlock != runtimeCore.RunningBlock)
                {
                    index = exe.runtimeSymbols[block].IndexOf(name, Constants.kGlobalScope, Constants.kGlobalScope);
                }

                if (index == Constants.kInvalidIndex)
                {
                    index = classnode.Symbols.IndexOfClass(name, ci, functionIndex);
                }

                if (index != Constants.kInvalidIndex)
                {
                    if (classnode.Symbols.symbolList[index].arraySizeList != null)
                    {
                        throw new NotImplementedException("{C5877FF2-968D-444C-897F-FE83650D5201}");
                    }
                    symbol = classnode.Symbols.symbolList[index];
                    return index;
                }
            }
            else
            {
                CodeBlock searchBlock = runtimeCore.DSExecutable.CompleteCodeBlocks[block];

                // To detal with the case that a language block defined in a function
                //
                // def foo()
                // {   
                //     [Imperative]
                //     {
                //          a;
                //     }
                // }
                if (functionIndex != ProtoCore.DSASM.Constants.kInvalidIndex)
                {
                    if (searchBlock.IsMyAncestorBlock(functionBlock))
                    {
                        while (searchBlock.codeBlockId != functionBlock)
                        {
                            index = exe.runtimeSymbols[searchBlock.codeBlockId].IndexOf(name, ci, ProtoCore.DSASM.Constants.kInvalidIndex);
                            if (index == ProtoCore.DSASM.Constants.kInvalidIndex)
                            {
                                searchBlock = searchBlock.parent;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }

                    if (index == ProtoCore.DSASM.Constants.kInvalidIndex)
                    {
                        index = exe.runtimeSymbols[searchBlock.codeBlockId].IndexOf(name, ci, functionIndex);
                    }

                    if (index == ProtoCore.DSASM.Constants.kInvalidIndex)
                    {
                        index = exe.runtimeSymbols[searchBlock.codeBlockId].IndexOf(name, ci, ProtoCore.DSASM.Constants.kInvalidIndex);
                    }
                }
                else
                {
                    index = exe.runtimeSymbols[searchBlock.codeBlockId].IndexOf(name, ci, ProtoCore.DSASM.Constants.kInvalidIndex);
                }

                if (index == ProtoCore.DSASM.Constants.kInvalidIndex)
                {
                    searchBlock = searchBlock.parent;
                    while (searchBlock != null)
                    {
                        block = searchBlock.codeBlockId;
                        index = exe.runtimeSymbols[searchBlock.codeBlockId].IndexOf(name, ci, ProtoCore.DSASM.Constants.kInvalidIndex);

                        if (index != ProtoCore.DSASM.Constants.kInvalidIndex)
                        {
                            break;
                        }
                        else
                        {
                            searchBlock = searchBlock.parent;
                        }
                    }
                }

                if (index != ProtoCore.DSASM.Constants.kInvalidIndex)
                {
                    if (exe.runtimeSymbols[searchBlock.codeBlockId].symbolList[index].arraySizeList != null)
                    {
                        throw new NotImplementedException("{C5877FF2-968D-444C-897F-FE83650D5201}");
                    }
                    block = searchBlock.codeBlockId;
                    symbol = exe.runtimeSymbols[searchBlock.codeBlockId].symbolList[index];
                    return index;
                }

                //if (block == 0)
                //{
                //    for (block = 0; block < exe.runtimeSymbols.Length; ++block)
                //    {
                //        index = exe.runtimeSymbols[block].IndexOf(name, ci, functionIndex);

                //        if (index != -1)
                //            break;
                //    }
                //}
                //else
                //{
                //    while (block >= 0)
                //    {
                //        index = exe.runtimeSymbols[block].IndexOf(name, ci, functionIndex);
                //        if (index != -1)
                //            break;
                //        else
                //            block--;

                //    }
                //}
            }
            throw new NameNotFoundException { Name = name };

            //throw new NotImplementedException("{F5ACC95F-AEC9-486D-BC82-FF2CB26E7E6A}"); //@TODO(Luke): Replace this with a symbol lookup exception
        }

        public string GetType(string name)
        {
            RuntimeMemory rmem = MirrorTarget.rmem;

            int classcope;
            int block = MirrorTarget.RuntimeCore.RunningBlock;
            SymbolNode symbol;
            int index = GetSymbolIndex(name, out classcope, ref block, out symbol);

            StackValue val;
            if (symbol.functionIndex == -1 && classcope != Constants.kInvalidIndex)
                val = rmem.GetMemberData(index, classcope, runtimeCore.DSExecutable);
            else
                val = rmem.GetSymbolValue(symbol);

            switch (val.optype)
            {
                case AddressType.Int:
                    return "int";
                case AddressType.Double:
                    return "double";
                case AddressType.Null:
                    return "null";
                case AddressType.Pointer:
                    {
                        int classtype = val.metaData.type;
                        ClassNode classnode = runtimeCore.DSExecutable.classTable.ClassNodes[classtype];
                        return classnode.Name;
                    }
                case AddressType.ArrayPointer:
                    return "array";
                case AddressType.Boolean:
                    return "bool";
                case AddressType.String:
                    return "string";
                default:
                    return "null"; // "Value not yet supported for tracing";
            }
        }

        // overload version of GetType which takes an Obj instead of string for IDE debuger use
        // usually the IDE has already get an instance of Obj before they call GetType
        // there is no need to look up that symbol again
        public string GetType(Obj obj)
        {
            if (obj.DsasmValue.IsPointer)
            {
                return runtimeCore.DSExecutable.classTable.ClassNodes[obj.DsasmValue.metaData.type].Name;
            }
            else
            {
                switch (obj.DsasmValue.optype)
                {
                    case AddressType.ArrayPointer:
                        return "array";
                    case AddressType.Int:
                        return "int";
                    case AddressType.Double:
                        return "double";
                    case AddressType.Null:
                        return "null";
                    case AddressType.Boolean:
                        return "bool";
                    case AddressType.String:
                        return "string";
                    case AddressType.Char:
                        return "char";
                    case AddressType.FunctionPointer:
                        return "function pointer";
                    default:
                        return null;
                }
            }
        }

        public Obj GetWatchValue()
        {
            RuntimeCore runtimeCore = MirrorTarget.RuntimeCore;
            int count = runtimeCore.watchStack.Count;
            int n = runtimeCore.WatchSymbolList.FindIndex(x => { return string.Equals(x.name, Constants.kWatchResultVar); });

            if (n < 0 || n >= count)
            {
                runtimeCore.WatchSymbolList.Clear();
                return new Obj { Payload = null };
            }

            Obj retVal = null;
            try
            {
                StackValue sv = runtimeCore.watchStack[n];
                if (!sv.IsInvalid)
                {
                    retVal = Unpack(runtimeCore.watchStack[n], MirrorTarget.rmem.Heap, runtimeCore);
                }
                else
                {
                    retVal = new Obj { Payload = null };
                }
            }
            catch
            {
                retVal = new Obj { Payload = null };
            }
            finally
            {
                runtimeCore.WatchSymbolList.Clear();
            }

            return retVal;
        }
        
        public Obj GetValue(string name, int block = 0, int classcope = Constants.kGlobalScope)
        {
            ProtoCore.DSASM.Executable exe = MirrorTarget.exe;

            int index = Constants.kInvalidIndex;
            if (block == 0)
            {
                for (block = 0; block < exe.runtimeSymbols.Length; ++block)
                {
                    index = exe.runtimeSymbols[block].IndexOf(name, classcope, Constants.kInvalidIndex);
                    if (index != Constants.kInvalidIndex)
                        break;
                }
            }
            else
            {
                index = exe.runtimeSymbols[block].IndexOf(name, classcope, Constants.kInvalidIndex);
            }

            if (Constants.kInvalidIndex == index)
            {
                throw new SymbolNotFoundException(name);
            }
            else
            {
                var symbol = exe.runtimeSymbols[block].symbolList[index];
                if (symbol.arraySizeList != null)
                {
                    throw new NotImplementedException("{C5877FF2-968D-444C-897F-FE83650D5201}");
                }

                Obj retVal = Unpack(MirrorTarget.rmem.GetSymbolValue(symbol), MirrorTarget.rmem.Heap, runtimeCore);

                return retVal;

            }
        }

        public void UpdateValue(int line, int index, int value)
        {
        }

        public void UpdateValue(int line, int index, double value)
        {
        }

        [Obsolete]
        private bool __Set_Value(string varName, int? value)
        {
            int blockId = 0;
            AssociativeGraph.GraphNode graphNode = MirrorTarget.GetFirstGraphNode(varName, out blockId);

            // There was no variable to set
            if (null == graphNode)
            {
                return false;
            }

            graphNode.isDirty = true;
            int startpc = graphNode.updateBlock.startpc;
            MirrorTarget.Modify_istream_entrypoint_FromSetValue(blockId, startpc);

            StackValue sv;
            if (null == value)
            {
                sv = StackValue.Null;
            }
            else
            {
                sv = StackValue.BuildInt((long)value);
            }
            MirrorTarget.Modify_istream_instrList_FromSetValue(blockId, startpc, sv);
            return true;
        }


        //
        //  1.	Get the graphnode given the varname
        //  2.	Get the sv of the symbol
        //  3.	set the sv to the new value
        //  4.	Get all graphnpodes dependent on the symbol and mark them dirty
        //  5.	Re-execute the script

        //  proc AssociativeEngine.SetValue(string varname, int block, StackValue, sv)
        //      symbol = dsEXE.GetSymbol(varname, block)
        //      globalStackIndex = symbol.stackindex
        //      runtime.stack[globalStackIndex] = sv
        //      AssociativeEngine.Propagate(symbol)
        //      runtime.Execute()
        //  end
        //

        private bool SetValue(string varName, int? value, out int nodesMarkedDirty)
        {
            int blockId = 0;

            // 1. Get the graphnode given the varname
            AssociativeGraph.GraphNode graphNode = MirrorTarget.GetFirstGraphNode(varName, out blockId);

            if (graphNode == null)
            {
                nodesMarkedDirty = 0;
                return false;
            }

            SymbolNode symbol = graphNode.updateNodeRefList[0].nodeList[0].symbol;

            // 2. Get the sv of the symbol
            int globalStackIndex = symbol.index;

            // 3. set the sv to the new value
            StackValue sv;
            if (null == value)
            {
                sv = StackValue.Null;
            }
            else
            {
                sv = StackValue.BuildInt((long)value);
            }
            MirrorTarget.rmem.Stack[globalStackIndex] = sv;

            // 4. Get all graphnpodes dependent on the symbol and mark them dirty
            const int outerBlock = 0;
            ProtoCore.DSASM.Executable exe = MirrorTarget.exe;
            List<AssociativeGraph.GraphNode> reachableGraphNodes = AssociativeEngine.Utils.UpdateDependencyGraph(
                graphNode, MirrorTarget, graphNode.exprUID, false, runtimeCore.Options.ExecuteSSA, outerBlock, false);

            // Mark reachable nodes as dirty
            Validity.Assert(reachableGraphNodes != null);
            nodesMarkedDirty = reachableGraphNodes.Count;
            foreach (AssociativeGraph.GraphNode gnode in reachableGraphNodes)
            {
                gnode.isDirty = true;
            }

            // 5. Re-execute the script - re-execution occurs after this call 

            return true;
        }

        public void NullifyVariable(string varName)
        {
            if (!string.IsNullOrEmpty(varName))
            {
                int nodesMarkedDirty = 0;
                SetValue(varName, null, out nodesMarkedDirty);
            }
        }


        /// <summary>
        /// Reset an existing value and re-execute the vm
        /// </summary>
        /// <param name="varName"></param>
        /// <param name="value"></param>
        public void SetValueAndExecute(string varName, int? value)
        {
            Executable exe = runtimeCore.DSExecutable;

            runtimeCore.Options.IsDeltaExecution = true;
            int nodesMarkedDirty = 0;
            bool wasSet = SetValue(varName, value, out nodesMarkedDirty);

            if (wasSet && nodesMarkedDirty > 0)
            {
                try
                {
                    foreach (ProtoCore.DSASM.CodeBlock codeblock in exe.CodeBlocks)
                    {
                        ProtoCore.DSASM.StackFrame stackFrame = new ProtoCore.DSASM.StackFrame(runtimeCore.RuntimeMemory.GlobOffset);
                        int locals = 0;

                        // Comment Jun: Tell the new bounce stackframe that this is an implicit bounce
                        // Register TX is used for this.
                        stackFrame.TX = StackValue.BuildCallingConversion((int)CallingConvention.BounceType.kImplicit);

                        runtimeCore.CurrentExecutive.CurrentDSASMExec.Bounce(
                            codeblock.codeBlockId, 
                            codeblock.instrStream.entrypoint, 
                            stackFrame,
                            locals);
                    }
                }
                catch
                {
                    throw;
                }
            }
        }

        public Obj GetDebugValue(string name)
        {
            int classcope;
            int block = runtimeCore.GetCurrentBlockId();

            RuntimeMemory rmem = MirrorTarget.rmem;
            SymbolNode symbol;
            int index = GetSymbolIndex(name, out classcope, ref block, out symbol);
            StackValue sv;
            if (symbol.functionIndex == -1 && classcope != Constants.kInvalidIndex)
                sv = rmem.GetMemberData(index, classcope, runtimeCore.DSExecutable);
            else
                sv = rmem.GetSymbolValue(symbol);

            if (sv.IsInvalid)
                throw new UninitializedVariableException { Name = name };

            return Unpack(sv);
        }

        // traverse an class type object to get its property
        public Dictionary<string, Obj> GetProperties(Obj obj, bool excludeStatic = false)
        {
            RuntimeMemory rmem = MirrorTarget.rmem;
            if (obj == null || !obj.DsasmValue.IsPointer)
                return null;

            Dictionary<string, Obj> ret = new Dictionary<string, Obj>();
            int classIndex = obj.DsasmValue.metaData.type;
            IDictionary<int,SymbolNode> symbolList = runtimeCore.DSExecutable.classTable.ClassNodes[classIndex].Symbols.symbolList;
            StackValue[] svs = rmem.Heap.ToHeapObject<DSObject>(obj.DsasmValue).Values.ToArray();
            int index = 0;
            for (int ix = 0; ix < svs.Length; ++ix)
            {
                if (excludeStatic && symbolList[ix].isStatic)
                    continue;
                string name = symbolList[ix].name;
                StackValue val = svs[index];

                // check if the members are primitive type
                if (val.IsPointer)
                {
                    var pointer = rmem.Heap.ToHeapObject<DSObject>(val);
                    var firstItem = pointer.Count == 1 ? pointer.GetValueFromIndex(0, runtimeCore) : StackValue.Null;
                    if (pointer.Count == 1 &&
                        !firstItem.IsPointer && 
                        !firstItem.IsArray)
                    {
                        val = firstItem;
                    }
                }

                ret[name] = Unpack(val);
                index++;
            }

            return ret;
        }

        public List<string> GetPropertyNames(Obj obj)
        {
            if (obj == null || !obj.DsasmValue.IsPointer)
                return null;

            List<string> ret = new List<string>();
            int classIndex = obj.DsasmValue.metaData.type;

            StackValue[] svs = MirrorTarget.rmem.Heap.ToHeapObject<DSObject>(obj.DsasmValue).Values.ToArray();
            for (int ix = 0; ix < svs.Length; ++ix)
            {
                string propertyName = runtimeCore.DSExecutable.classTable.ClassNodes[classIndex].Symbols.symbolList[ix].name;
                ret.Add(propertyName);
            }

            return ret;
        }

        // traverse an array Obj return its member
        public List<Obj> GetArrayElements(Obj obj)
        {
            if ( obj == null || !obj.DsasmValue.IsArray)
                return null;

            return MirrorTarget.rmem.Heap.ToHeapObject<DSArray>(obj.DsasmValue).Values.Select(x => Unpack(x)).ToList();
        }

        public StackValue GetGlobalValue(string name, int startBlock = 0)
        {
            ProtoCore.DSASM.Executable exe = MirrorTarget.exe;

            for (int block = startBlock; block < exe.runtimeSymbols.Length; block++)
            {
                int index = exe.runtimeSymbols[block].IndexOf(name, Constants.kInvalidIndex, Constants.kGlobalScope);
                if (Constants.kInvalidIndex != index)
                {
                    //Q(Luke): This seems to imply that the LHS is an array index?
                    if (exe.runtimeSymbols[block].symbolList[index].arraySizeList != null)
                    {
                        throw new NotImplementedException("{C5877FF2-968D-444C-897F-FE83650D5202}");
                    }

                    SymbolNode symNode = exe.runtimeSymbols[block].symbolList[index];
                    if (symNode.absoluteFunctionIndex == Constants.kGlobalScope)
                    {
                        return MirrorTarget.rmem.GetAtRelative(symNode.index);
                    }
                }
            }
            return StackValue.Null;
        }

        public StackValue GetRawFirstValue(string name, int startBlock = 0, int classcope = Constants.kGlobalScope)
        {
            ProtoCore.DSASM.Executable exe = MirrorTarget.exe;

            for (int block = startBlock; block < exe.runtimeSymbols.Length; block++)
            {
                int index = exe.runtimeSymbols[block].IndexOf(name, classcope, Constants.kGlobalScope);
                if (Constants.kInvalidIndex != index)
                {
                    //Q(Luke): This seems to imply that the LHS is an array index?
                    var symbol = exe.runtimeSymbols[block].symbolList[index];
                    if (symbol.arraySizeList != null)
                    {
                        throw new NotImplementedException("{C5877FF2-968D-444C-897F-FE83650D5201}");
                    }

                    return MirrorTarget.rmem.GetSymbolValue(symbol);
                }
            }
            throw new NotImplementedException("{F5ACC95F-AEC9-486D-BC82-FF2CB26E7E6A}"); //@TODO(Luke): Replace this with a symbol lookup exception
        }

        public string GetFirstNameFromValue(StackValue v)
        {
            if (!v.IsPointer)
                throw new ArgumentException("SV to highlight must be a pointer");

            ProtoCore.DSASM.Executable exe = MirrorTarget.exe;

            List<SymbolNode> symNodes = new List<SymbolNode>();

            foreach (SymbolTable symTable in exe.runtimeSymbols)
            {
                foreach (SymbolNode symNode in symTable.symbolList.Values)
                {
                    symNodes.Add(symNode);
                }

            }


            int index = MirrorTarget.rmem.Stack.FindIndex(0, value => value.opdata == v.opdata);

            List<SymbolNode> matchingNodes = symNodes.FindAll(value => value.index == index);

            if (matchingNodes.Count > 0)
                return matchingNodes[0].name;
            else
            {
                return null;
            }
        }


        public Obj GetFirstValue(string name, int startBlock = 0, int classcope = Constants.kGlobalScope)
        {
            Obj retVal = Unpack(GetRawFirstValue(name, startBlock, classcope), MirrorTarget.rmem.Heap, runtimeCore);
            return retVal;
        }


        //@TODO(Luke): Add in the methods here that correspond to each of the internal datastructures in use by the executive
        //@TODO(Jun): if this method stays static, then the Heap needs to be referenced from a parameter
        /// <summary>
        /// Do the recursive unpacking of the data structure into mirror objects
        /// </summary>
        /// <param name="val"></param>
        /// <returns></returns>
        public static Obj Unpack(StackValue val, Heap heap, RuntimeCore runtimeCore, int type = (int)PrimitiveType.kTypePointer) 
        {
            Executable exe = runtimeCore.DSExecutable;
            switch (val.optype)
            {
                case AddressType.ArrayPointer:
                    {
                        DsasmArray ret = new DsasmArray();

                        //Pull the item out of the heap


                        var array = heap.ToHeapObject<DSArray>(val);

                        StackValue[] nodes = array.Values.ToArray();
                        ret.members = new Obj[array.Count];
                        for (int i = 0; i < ret.members.Length; i++)
                        {
                            ret.members[i] = Unpack(nodes[i], heap, runtimeCore, type);
                        }

                        // TODO Jun: ret.members[0] is hardcoded  and means we are assuming a homogenous collection
                        // How to handle mixed-type arrays?
                        Obj retO = new Obj(val) 
                        { 
                            Payload = ret, 
                            Type = exe.TypeSystem.BuildTypeObject(
                                        (ret.members.Length > 0)
                                        ? exe.TypeSystem.GetType(ret.members[0].Type.Name) 
                                        : (int)ProtoCore.PrimitiveType.kTypeVoid, Constants.kArbitraryRank) 
                        };

                        return retO;
                    }
                case AddressType.String:
                    {
                        string str = heap.ToHeapObject<DSString>(val).Value;
                        Obj o = new Obj(val)
                        {
                            Payload = str,
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeString, 0)
                        };
                        return o;
                    }
                case AddressType.Int:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeInt, 0) 
                        };
                        return o;
                    }
                case AddressType.Boolean:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = (data != 0), 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeBool, 0) 
                        };
                        return o;
                    }

                case AddressType.Null:
                    {
                        Obj o = new Obj(val) 
                        { 
                            Payload = null, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeNull, 0) 
                        };
                        return o;
                    }
                case AddressType.Char:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        {
                            Payload = data, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeChar, 0) 
                        };
                        return o;
                    }
                case AddressType.Double:
                    {
                        double data = val.RawDoubleValue;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data, Type =
                            TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeDouble, 0) 
                        };
                        return o;
                    }
                case AddressType.Pointer:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data,
                            Type = exe.TypeSystem.BuildTypeObject(type, 0) 
                        };
                        return o;
                    }
                case AddressType.FunctionPointer:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeFunctionPointer, 0) 
                        };
                        return o;
                    }
                case AddressType.Invalid:
                    {
                        return new Obj(val) {Payload = null};
                    }
                default:
                    {
                        throw new NotImplementedException(string.Format("unknown datatype {0}", val.optype.ToString()));
                    }
            }

        }

        public static Obj Unpack(StackValue val, RuntimeCore runtimeCore)
        {
            RuntimeMemory rmem = runtimeCore.RuntimeMemory;
            Executable exe = runtimeCore.DSExecutable;
            switch (val.optype)
            {
                case AddressType.ArrayPointer:
                    {
                        //It was a pointer that we pulled, so the value lives on the heap
                        DsasmArray ret = new DsasmArray();
                        var array = rmem.Heap.ToHeapObject<DSArray>(val);

                        StackValue[] nodes = array.Values.ToArray();
                        ret.members = new Obj[nodes.Length];

                        for (int i = 0; i < ret.members.Length; i++)
                        {
                            ret.members[i] = Unpack(nodes[i], runtimeCore);
                        }

                        Obj retO = new Obj(val) 
                        { 
                            Payload = ret,
                            Type = exe.TypeSystem.BuildTypeObject((ret.members.Length > 0) ? exe.TypeSystem.GetType(ret.members[0].Type.Name) : (int)ProtoCore.PrimitiveType.kTypeVar, Constants.kArbitraryRank)
                        };

                        return retO;
                    }
                case AddressType.Int:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeInt, 0) 
                        };
                        return o;
                    }
                case AddressType.Boolean:
                    {
                        Obj o = new Obj(val) 
                        { 
                            Payload = val.opdata == 0 ? false : true, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeBool, 0) 
                        };
                        return o;
                    }
                case AddressType.Null:
                    {
                        Obj o = new Obj(val) 
                        { 
                            Payload = null, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeNull, 0) 
                        };
                        return o;
                    }
                case AddressType.Double:
                    {
                        double data = val.RawDoubleValue;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeDouble, 0) 
                        };
                        return o;
                    }
                case AddressType.Char:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeChar, 0) 
                        };
                        return o;
                    }
                case AddressType.Pointer:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data,
                            Type = exe.TypeSystem.BuildTypeObject(val.metaData.type, 0) 
                        };
                        return o;
                    }
                case AddressType.DefaultArg:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeVar, 0) 
                        };
                        return o;
                    }
                case AddressType.FunctionPointer:
                    {
                        Int64 data = val.opdata;
                        Obj o = new Obj(val) 
                        { 
                            Payload = data, 
                            Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeFunctionPointer, 0) 
                        };
                        return o;
                    }
                default:
                    {
                        throw new NotImplementedException(string.Format("unknown datatype {0}", val.optype.ToString()));
                    }
            }

        }

        // this method is used for the IDE to query object values 
        // Copy from the the existing Unpack with some modifications
        //  1: It is a non-static method so there is no need to pass the core and heap
        //  2: It does not traverse the array, array traverse is done in method GetArrayElement
        //  3: The payload for boolean and null is changed to Boolean and null type in .NET, such that the watch windows can directly call ToString() 
        //     to print the value, otherwize for boolean it will print either 0 or 1, for null it will print 0
        public Obj Unpack(StackValue val)
        {
            Obj obj = null;

            switch (val.optype)
            {
                case AddressType.Pointer:
                    obj = new Obj(val) 
                    { 
                        Payload = val.opdata, 
                        Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypePointer, 0) 
                    };
                    break;
                case AddressType.ArrayPointer:
                    obj = new Obj(val) 
                    { 
                        Payload = val.opdata, 
                        Type =
                        TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeArray, Constants.kArbitraryRank)
                    };
                    break;
                case AddressType.Int:
                    obj = new Obj(val) 
                    { 
                        Payload = val.opdata, 
                        Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeInt, 0) 
                    };
                    break;
                case AddressType.Boolean:
                    obj = new Obj(val) 
                    { 
                        Payload = val.opdata == 0 ? false : true, 
                        Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeBool, 0) 
                    };
                    break;
                case AddressType.Double:
                    obj = new Obj(val) 
                    { 
                        Payload = val.RawDoubleValue, 
                        Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeDouble, 0) 
                    };
                    break;
                case AddressType.Null:
                    obj = new Obj(val) 
                    { 
                        Payload = null, 
                        Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeNull, 0) 
                    };
                    break;
                case AddressType.FunctionPointer:
                    obj = new Obj(val) 
                    { 
                        Payload = val.opdata, 
                        Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeFunctionPointer, 0) 
                    };
                    break;
                case AddressType.String:
                    obj = new Obj(val) 
                    { 
                        Payload = val.opdata, 
                        Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeString, Constants.kPrimitiveSize) 
                    };
                    break;
                case AddressType.Char:
                    obj = new Obj(val) 
                    { 
                        Payload = val.opdata, 
                        Type = TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeChar, 0) 
                    };
                    break;
            }

            return obj;
        }

        public static StackValue Repack(Obj obj, ProtoCore.DSASM.Heap heap)
        {
            if (obj.Type.IsIndexable)
            {
                //Unpack each of the elements
                DsasmArray arr = (DsasmArray)obj.Payload;

                StackValue[] sv = new StackValue[arr.members.Length];

                //recurse over the array
                for (int i = 0; i < sv.Length; i++)
                    sv[i] = Repack(arr.members[i], heap);

                int size = sv.Length;

                StackValue ptr = heap.AllocateArray(sv);
                return ptr;
            }

            // For non-arrays, there is nothing to repack so just return the original stackvalue
            return obj.DsasmValue;
        }

        public bool CompareArrays(DsasmArray dsArray, List<Object> expected, System.Type type)
        {
            if (dsArray.members.Length != expected.Count)
                return false;

            for (int i = 0; i < dsArray.members.Length; ++i)
            {
                List<Object> subExpected = expected[i] as List<Object>;
                DsasmArray subArray = dsArray.members[i].Payload as DsasmArray;

                if ((subExpected != null) && (subArray != null)) {

                    if (!CompareArrays(subArray, subExpected, type))
                        return false;
                }
                else if ((subExpected == null) && (subArray == null))
                {
                    if (type == typeof(Int64))
                    {
                        if (Convert.ToInt64(dsArray.members[i].Payload) != Convert.ToInt64(expected[i]))
                            return false;
                    }
                    else if (type == typeof(Double))
                    {
                        // can't use Double.Episilion, according to msdn, it is smaller than most
                        // errors.
                        if (Math.Abs(Convert.ToDouble(dsArray.members[i].Payload) - Convert.ToDouble(expected[i])) > 0.000001)
                            return false;
                    }
                    else if (type == typeof(Boolean))
                    {
                        if (Convert.ToBoolean(dsArray.members[i].Payload) != Convert.ToBoolean(expected[i]))
                            return false;
                    }
                    else if (type == typeof(Char))
                    {
                        object payload = dsArray.members[i].Payload;
                        return Convert.ToChar(Convert.ToInt64(payload)) == Convert.ToChar(expected[i]);
                    }
                    else if (type == typeof(String))
                    {
                        return Convert.ToString(dsArray.members[i].Payload) == Convert.ToString(expected[i]);
                    }
                    else
                    {
                        throw new NotImplementedException("Test comparison not implemented: {EBAFAE6C-BCBF-42B8-B99C-49CFF989F0F0}");
                    }
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        public bool CompareArrays(string mirrorObj, List<Object> expected, System.Type type, int blockIndex = 0)
        {
            DsasmArray computedArray = GetValue(mirrorObj, blockIndex).Payload as DsasmArray;
            return CompareArrays(computedArray, expected, type);
        }

        public bool EqualDotNetObject(Obj dsObj, object dotNetObj)
        {
            // check for null first
            if (dotNetObj == null)
            {
                if (dsObj.DsasmValue.IsNull)
                    return true;
                else
                    return false;
            }

            System.Type t = dotNetObj.GetType();
            switch (dsObj.DsasmValue.optype)
            {
                case AddressType.ArrayPointer:
                    if (t.IsArray)
                    {
                        object[] dotNetValue = (object[])dotNetObj;
                        Obj[] dsValue = GetArrayElements(dsObj).ToArray();

                        if (dotNetValue.Length == dsValue.Length)
                        {
                            for (int ix = 0; ix < dsValue.Length; ++ix)
                            {
                                if (!EqualDotNetObject(dsValue[ix], dotNetValue[ix]))
                                    return false;
                            }
                            return true;
                        }
                    }
                    return false;
                case AddressType.Int:
                    if (dotNetObj is int)
                        return (Int64)dsObj.Payload == (int)dotNetObj;
                    else
                        return false;
                case AddressType.Double:
                    if (dotNetObj is double)
                        return (Double)dsObj.Payload == (Double)dotNetObj;
                    else
                        return false;
                case AddressType.Boolean:
                    if (dotNetObj is bool)
                        return (Boolean)dsObj.Payload == (Boolean)dotNetObj;
                    else
                        return false;
                case AddressType.Pointer:
                    if (t == typeof(Dictionary<string, Object>))
                    {
                        Dictionary<string, Obj> dsProperties = GetProperties(dsObj);
                        foreach (KeyValuePair<string, object> dotNetProperty in dotNetObj as Dictionary<string, object>)
                        {
                            if (!(dsProperties.ContainsKey(dotNetProperty.Key) && EqualDotNetObject(dsProperties[dotNetProperty.Key], dotNetProperty.Value)))
                                return false;
                        }
                        return true;
                    }
                    return false;
                case AddressType.Null:
                    return dotNetObj == null;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    class OutputFormatParameters
    {
        private int maximumDepth = -1;
        private int maximumArray = -1;

        internal OutputFormatParameters()
        {
            this.CurrentOutputDepth = -1;
        }

        internal OutputFormatParameters(int maximumArray, int maximumDepth)
        {
            this.maximumArray = maximumArray;
            this.maximumDepth = maximumDepth;
            this.CurrentOutputDepth = this.maximumDepth;
        }

        internal void ResetOutputDepth()
        {
            this.CurrentOutputDepth = this.maximumDepth;
        }

        internal bool ContinueOutputTrace()
        {
            // No output depth specified.
            if (-1 == maximumDepth)
                return true;

            // Previously reached zero, don't keep decreasing because that 
            // will essentially reach -1 and depth control will be disabled.
            if (0 == CurrentOutputDepth)
                return false;

            // Discontinue if we reaches zero.
            CurrentOutputDepth--;
            return (0 != CurrentOutputDepth);
        }

        internal void RestoreOutputTraceDepth()
        {
            if (-1 == maximumDepth)
                return;

            CurrentOutputDepth++;
        }

        internal int MaxArraySize { get { return maximumArray; } }
        internal int MaxOutputDepth { get { return maximumDepth; } }
        internal int CurrentOutputDepth { get; private set; }
    }

    public class NameNotFoundException : Exception
    {
        public string Name { get; set; }
    }
    public class UninitializedVariableException : Exception
    {
        public string Name { get; set; }
    }

    //@TODO(Luke): turn this into a proper shadow array representation
    
    public class DsasmArray
    {
        public Obj[] members;

    }

}
