﻿using System;
using System.Collections.Generic;
using System.Linq;
using Dynamo.Engine;
using Dynamo.Engine.CodeGeneration;
using Dynamo.Graph;
using Dynamo.Graph.Nodes;
using Dynamo.Graph.Nodes.CustomNodes;
using Dynamo.Library;
using Dynamo.Models;
using ProtoCore.AST.AssociativeAST;
using ProtoCore;

namespace Dynamo
{
    /// <summary>
    ///     Compiler definition of a Custom Node.
    /// </summary>
    public class CustomNodeDefinition : IFunctionDescriptor
    {
        public CustomNodeDefinition(
            Guid functionId,
            string displayName="",
            IEnumerable<NodeModel> nodeModels=null)
        {
            if (functionId == Guid.Empty)
                throw new ArgumentException(@"FunctionId invalid.", "functionId");

            nodeModels = nodeModels ?? new List<NodeModel>();

            #region Find outputs

            // Find output elements for the node

            var outputs = nodeModels.OfType<Output>().ToList();

            var topMost = new List<Tuple<int, NodeModel>>();

            List<string> outNames;
            returns = new List<Tuple<string, string>>();

            // if we found output nodes, add select their inputs
            // these will serve as the function output
            if (outputs.Any())
            {
                topMost.AddRange(
                    outputs.Where(x => x.HasInput(0)).Select(x => Tuple.Create(0, x as NodeModel)));
                returns = outputs.Select(x => x.Return).ToList();
            }
            else
            {
                // if there are no explicitly defined output nodes
                // get the top most nodes and set THEM as the output
                IEnumerable<NodeModel> topMostNodes = nodeModels.Where(node => node.IsTopMostNode);

                var rtnPorts =
                    //Grab multiple returns from each node
                    topMostNodes.SelectMany(
                        topNode =>
                            //If the node is a recursive instance...
                            topNode is Function && (topNode as Function).Definition.FunctionId == functionId
                                // infinity output
                                ? new[] {new {portIndex = 0, node = topNode, name = "∞"}}
                                // otherwise, grab all ports with connected outputs and package necessary info
                                : topNode.OutPortData
                                    .Select(
                                        (port, i) =>
                                            new {portIndex = i, node = topNode, name = port.NickName})
                                    .Where(x => !topNode.HasOutput(x.portIndex)));

                foreach (var rtnAndIndex in rtnPorts.Select((rtn, i) => new {rtn, idx = i}))
                {
                    topMost.Add(Tuple.Create(rtnAndIndex.rtn.portIndex, rtnAndIndex.rtn.node));
                    var outName = rtnAndIndex.rtn.name ?? rtnAndIndex.idx.ToString();
                    returns.Add(new Tuple<string, string>(outName, string.Empty));
                }
            }

            var nameDict = new Dictionary<string, int>();
            foreach (var name in returns.Select(p => p.Item1))
            {
                if (nameDict.ContainsKey(name))
                    nameDict[name]++;
                else
                    nameDict[name] = 0;
            }

            nameDict = nameDict.Where(x => x.Value != 0).ToDictionary(x => x.Key, x => x.Value);

            returns.Reverse();

            var returnKeys = new List<string>();
            for (int i = 0; i < returns.Count(); ++i)
            {
                var info = returns[i];
                int amt;
                var name = info.Item1;

                if (nameDict.TryGetValue(name, out amt))
                {
                    nameDict[name] = amt - 1;
                    var newName = string.IsNullOrEmpty(name) ? amt + ">" : name + amt;

                    returnKeys.Add(newName);
                    returns[i] = new Tuple<string, string>(newName, info.Item2);
                }
                else
                    returnKeys.Add(name);
            }

            returnKeys.Reverse();
            returns.Reverse();

            #endregion

            #region Find inputs

            //Find function entry point, and then compile
            var inputNodes = nodeModels.OfType<Symbol>().ToList();
            var parameters = inputNodes.Select(x => new TypedParameter(
                                                   x.GetAstIdentifierForOutputIndex(0).Value,
                                                   x.Parameter.Type, 
                                                   x.Parameter.DefaultValue,
                                                   null,
                                                   x.Parameter.Summary));
            var displayParameters = inputNodes.Select(x => x.Parameter.Name);

            #endregion

            FunctionBody = nodeModels.Where(node => !(node is Symbol));
            DisplayName = displayName;
            FunctionId = functionId;
            Parameters = parameters;
            ReturnKeys = returnKeys;
            DisplayParameters = displayParameters;
            OutputNodes = topMost.Select(x => x.Item2.GetAstIdentifierForOutputIndex(x.Item1));
            DirectDependencies = nodeModels
                .OfType<Function>()
                .Select(node => node.Definition)
                .Where(def => def.FunctionId != functionId)
                .Distinct();
            ReturnType = ProtoCore.TypeSystem.BuildPrimitiveTypeObject(PrimitiveType.kTypeVar);
        }

        internal static CustomNodeDefinition MakeProxy(Guid functionId, string displayName)
        {
            var def = new CustomNodeDefinition(functionId, displayName);
            def.IsProxy = true;
            return def;
        }

        /// <summary>
        ///     Is this CustomNodeDefinition properly loaded?
        /// </summary>
        public bool IsProxy { get; private set; }

        /// <summary>
        ///     Function name.
        /// </summary>
        public string FunctionName
        {
            get { return AstBuilder.StringConstants.FunctionPrefix + 
                         FunctionId.ToString().Replace("-", string.Empty); }
        }

        /// <summary>
        ///     Function unique ID.
        /// </summary>
        public Guid FunctionId { get; private set; }

        /// <summary>
        ///     User-friendly parameters
        /// </summary>
        public IEnumerable<string> DisplayParameters { get; private set; }

        /// <summary>
        ///     Function parameters.
        /// </summary>
        public IEnumerable<TypedParameter> Parameters { get; private set; } 

        /// <summary>
        ///     If the function returns a dictionary, this specifies all keys in
        ///     that dictionary.
        /// </summary>
        public IEnumerable<string> ReturnKeys { get; private set; }

        /// <summary>
        ///     NodeModels making up the body of the custom node.
        /// </summary>
        public IEnumerable<NodeModel> FunctionBody { get; private set; }

        /// <summary>
        ///     Identifiers associated with the outputs of the custom node.
        /// </summary>
        public IEnumerable<AssociativeNode> OutputNodes { get; private set; }
        
        /// <summary>
        ///     User friendly name on UI.
        /// </summary>
        public string DisplayName { get; private set; }

        /// <summary>
        ///     Return type.
        /// </summary>
        public ProtoCore.Type ReturnType { get; private set; }

        private List<Tuple<string, string>> returns;
        /// <summary>
        ///     The collection of output name and its description.
        /// </summary>
        public IEnumerable<Tuple<string, string>> Returns
        {
            get
            {
                return returns;
            }
        }

        #region Dependencies

        public IEnumerable<CustomNodeDefinition> Dependencies
        {
            get { return FindAllDependencies(new HashSet<CustomNodeDefinition>()); }
        }

        public IEnumerable<CustomNodeDefinition> DirectDependencies { get; private set; }
        
        private IEnumerable<CustomNodeDefinition> FindAllDependencies(ISet<CustomNodeDefinition> dependencySet)
        {
            var query = DirectDependencies.Where(def => !dependencySet.Contains(def));

            foreach (var definition in query)
            {
                yield return definition;
                dependencySet.Add(definition);
                foreach (var def in definition.FindAllDependencies(dependencySet))
                    yield return def;
            }
        }

        #endregion

        #region IFunctionDescriptor Members

        /// <summary>
        /// Name to create custom node
        /// </summary>
        public string MangledName
        {
            get { return FunctionId.ToString(); }
        }

        #endregion
    }
    
    /// <summary>
    ///     Basic information about a custom node.
    /// </summary>
    public class CustomNodeInfo
    {
        public CustomNodeInfo(Guid functionId, string name, string category, string description, string path, bool isVisibleInDynamoLibrary = true)
        {
            if (functionId == Guid.Empty)
                throw new ArgumentException(@"FunctionId invalid.", "functionId");
            
            FunctionId = functionId;
            Name = name;
            Description = description;
            Path = path;
            IsVisibleInDynamoLibrary = isVisibleInDynamoLibrary;

            Category = category;
            if (String.IsNullOrWhiteSpace(Category))
                Category = Dynamo.Properties.Resources.DefaultCustomNodeCategory;
        }

        public Guid FunctionId { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public string Path { get; set; }
        public bool IsPackageMember { get; set; }
        public bool IsVisibleInDynamoLibrary { get; private set; }
    }
}
