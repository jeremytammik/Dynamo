﻿using System.Collections.Generic;
using System.Linq;

namespace ProtoCore.DSASM
{ 
    [System.Diagnostics.DebuggerDisplay("{name}, fi = {functionIndex}, ci = {classScope}, block = {runtimeTableIndex}")]
    public class SymbolNode
    {
        public string           name;
        public string           forArrayName;
        public int              index;
        public int              heapIndex;
        public int              classScope;
        public int              functionIndex;
        public int              absoluteClassScope;
        public int              absoluteFunctionIndex;
        public ProtoCore.Type   datatype;
        public ProtoCore.Type   staticType;
        
        public bool             isArgument;
        public bool             isTemp;
        public int              size;
        public int              datasize;
        public List<int>        arraySizeList;
        public MemoryRegion     memregion;
        public int              symbolTableIndex = Constants.kInvalidIndex;
        public int              runtimeTableIndex = Constants.kInvalidIndex;
        public CompilerDefinitions.AccessModifier  access;
        public bool isStatic;
        public List<AttributeEntry> Attributes { get; set; }
        public int codeBlockId = Constants.kInvalidIndex;
        public string ExternLib = "";

        public SymbolNode()
        {
            name = string.Empty;
            arraySizeList   = null;
            memregion       = MemoryRegion.kInvalidRegion;
            classScope      = Constants.kInvalidIndex;
            functionIndex   = Constants.kGlobalScope;
            absoluteClassScope = Constants.kGlobalScope;
            absoluteFunctionIndex = Constants.kGlobalScope;
            isStatic        = false;
            isTemp          = false;
        }

        public SymbolNode(
            string name,
            int index, 
            int heapIndex, 
            int functionIndex,
            ProtoCore.Type datatype,
            ProtoCore.Type enforcedType,
            int size,
            int datasize, 
            bool isArgument, 
            int runtimeIndex,
            MemoryRegion memregion = MemoryRegion.kInvalidRegion, 
            bool isArray = false, 
            List<int> arraySizeList = null, 
            int scope = -1,
            ProtoCore.CompilerDefinitions.AccessModifier access = ProtoCore.CompilerDefinitions.AccessModifier.kPublic,
            bool isStatic = false,
            int codeBlockId = ProtoCore.DSASM.Constants.kInvalidIndex)
        {
            this.name           = name;
            isTemp         = name.StartsWith("%");
            this.index          = index;
            this.functionIndex = functionIndex;
            this.absoluteFunctionIndex = functionIndex;
            this.datatype       = datatype;
            this.staticType   = enforcedType;
            this.size           = size;
            this.datasize       = datasize;
            this.isArgument     = isArgument;
            this.arraySizeList  = arraySizeList;
            this.memregion      = memregion;
            this.classScope     = scope;
            this.absoluteClassScope = scope;
            runtimeTableIndex = runtimeIndex;
            this.access = access;
            this.isStatic = isStatic;
            this.codeBlockId = codeBlockId;
        }

        public override bool Equals(object obj)
        {
            var rhs = obj as SymbolNode;
            if (rhs == null)
            {
                return false;
            }

            return name.Equals(rhs.name) &&
                   functionIndex == rhs.functionIndex && 
                   classScope == rhs.classScope && 
                   codeBlockId == rhs.codeBlockId;
        }

        public void SetStaticType(ProtoCore.Type newtype)
        {
            if (staticType.Equals(newtype))
            {
                return;
            }

            staticType = newtype;
            if (staticType.UID != (int)PrimitiveType.kTypeVar || staticType.rank != 0)
            {
                datatype = staticType;
            }
        }
    }

    public class SymbolTable
    {
        private int size;
        private SortedList<int, SymbolNode> symbols = new SortedList<int,SymbolNode>();
        private Dictionary<string, List<SymbolNode>> lookAsideSymbolCache = new Dictionary<string, List<SymbolNode>>();  

        public string ScopeName 
        { 
            get; 
            set; 
        }
        
        public int RuntimeIndex 
        { 
            get; 
            set; 
        }

        public IDictionary<int,SymbolNode> symbolList
        {
            get
            {
                return symbols;
            }
        }

        /// <summary>
        /// Method to undefine a symbol from the symboltable entry and cache
        /// </summary>
        /// <param name="symbol"></param>
        public void UndefineSymbol(SymbolNode symbol)
        {
            // Dont remove from symbol table, but just nullify it.
            symbolList[symbol.symbolTableIndex] = new SymbolNode();

            if (lookAsideSymbolCache.ContainsKey(symbol.name))
            {
                List<SymbolNode> cachedSymbolList = lookAsideSymbolCache[symbol.name];
                for (int n = 0; n < cachedSymbolList.Count; ++n)
                {
                    SymbolNode cachedSymbol = cachedSymbolList[n];
                    if (cachedSymbol.name.Equals(symbol.name) && cachedSymbol.classScope == symbol.classScope && cachedSymbol.functionIndex == symbol.functionIndex)
                    {
                        // Dont remove from symbol table, but just nullify it.
                        cachedSymbolList[n] = new SymbolNode();
                        break;
                    }
                }
            }
        }

        public SymbolTable(string scopeName, int runtimeIndex)
        {
            size = 0;
            ScopeName = scopeName;
            RuntimeIndex = runtimeIndex;
        }

        public int GetGlobalSize()
        {
            return size;
        }

        // TODO Jun: return failed status
        public int Append( SymbolNode node )
        {
            if (IndexOf(node) != Constants.kInvalidIndex)
                return Constants.kInvalidIndex;

            int symbolTableIndex = symbolList.Count;
            node.symbolTableIndex = symbolTableIndex;
            symbolList[symbolTableIndex] = node;
            if (Constants.kGlobalScope == node.functionIndex)
            {
                size += node.size;
            }

            if (!lookAsideSymbolCache.ContainsKey(node.name))
                lookAsideSymbolCache.Add(node.name, new List<SymbolNode>());

            lookAsideSymbolCache[node.name].Add(node);

            return symbolTableIndex;
        }

        public bool Remove(SymbolNode node)
        {
            if (lookAsideSymbolCache.ContainsKey(node.name))
                if (lookAsideSymbolCache[node.name].Contains(node))
                    lookAsideSymbolCache[node.name].Remove(node);

            return symbolList.Remove(node.symbolTableIndex);

        }

        public IEnumerable<SymbolNode> GetNodeForName(string name)
        {
            return symbolList.Values.Where(n => string.Equals(n.name, name));
        }

        public int IndexOf(SymbolNode symbol)
        {
            return symbol.symbolTableIndex;
        }

        public int IndexOf(string name)
        {
            var symbol = symbols.Values.FirstOrDefault(x => string.Equals(x.name, name));

            if (null == symbol)
                return ProtoCore.DSASM.Constants.kInvalidIndex;
            else
                return symbol.symbolTableIndex;
        }

        public int IndexOf(string name, int classScope)
        {
            var symbol = symbols.Values.FirstOrDefault(x =>
                                            string.Equals(x.name, name)
                                        && x.classScope == classScope);

            if (null == symbol)
                return ProtoCore.DSASM.Constants.kInvalidIndex;
            else
                return symbol.symbolTableIndex;
        }

        public int IndexOf(string name, int classScope, int functionindex)
        {

            if (!lookAsideSymbolCache.ContainsKey(name))
                return ProtoCore.DSASM.Constants.kInvalidIndex;  

            var symbol = lookAsideSymbolCache[name].FirstOrDefault(x =>
                                            string.Equals(x.name, name)
                                        && x.classScope == classScope
                                        && x.functionIndex == functionindex);

           if (symbol == null)
                return ProtoCore.DSASM.Constants.kInvalidIndex;
            else
                return symbol.symbolTableIndex;
        }

        public int IndexOfClass(string name, int classScope, int functionindex)
        {
            foreach (var symbol in symbols.Values)
            {
                if (symbol.name != name)
                    continue;

                if (symbol.functionIndex == -1)
                {
                    return symbol.symbolTableIndex;
                }
                else if (classScope == symbol.classScope && functionindex == symbol.functionIndex)
                {
                    return symbol.symbolTableIndex;
                }
            }
            return Constants.kInvalidIndex;
        }
    }
}
