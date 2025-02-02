﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml;
using Autodesk.DesignScript.Interfaces;
using Dynamo.Engine;
using Dynamo.Engine.CodeGeneration;
using Dynamo.Graph.Connectors;
using Dynamo.Graph.Nodes.CustomNodes;
using Dynamo.Graph.Nodes.ZeroTouch;
using Dynamo.Migration;
using Dynamo.Scheduler;
using Dynamo.Selection;
using Dynamo.Utilities;
using Dynamo.Visualization;
using ProtoCore.AST.AssociativeAST;
using ProtoCore.DSASM;
using ProtoCore.Mirror;
using String = System.String;
using StringNode = ProtoCore.AST.AssociativeAST.StringNode;

namespace Dynamo.Graph.Nodes
{
    public abstract class NodeModel : ModelBase, IRenderPackageSource<NodeModel>, IDisposable
    {
        #region private members

        private bool overrideNameWithNickName;
        private LacingStrategy argumentLacing = LacingStrategy.First;
        private bool displayLabels;
        private bool isUpstreamVisible;
        private bool isVisible;
        private bool canUpdatePeriodically;
        private string nickName;
        private ElementState state;
        private string toolTipText = "";
        private string description;
        private string persistentWarning = "";
        private bool areInputPortsRegistered;
        private bool areOutputPortsRegistered;

        ///A flag indicating whether the node has been explicitly frozen.
        internal bool isFrozenExplicitly;

        /// <summary>
        /// The cached value of this node. The cachedValue object is protected by the cachedValueMutex
        /// as it may be accessed from multiple threads concurrently. 
        /// 
        /// However, generally access to the cachedValue property should be protected by usage
        /// of the Scheduler. 
        /// </summary>
        private MirrorData cachedValue;
        private readonly object cachedValueMutex = new object();

        // Input and output port related data members.
        private ObservableCollection<PortModel> inPorts = new ObservableCollection<PortModel>();
        private ObservableCollection<PortModel> outPorts = new ObservableCollection<PortModel>();
        private readonly Dictionary<PortModel, PortData> portDataDict = new Dictionary<PortModel, PortData>();

        #endregion

        #region public members

        private readonly Dictionary<int, Tuple<int, NodeModel>> inputNodes;
        private readonly Dictionary<int, HashSet<Tuple<int, NodeModel>>> outputNodes;

        /// <summary>
        /// The unique name that was created the node by
        /// </summary>
        public virtual string CreationName { get { return this.Name; } }

        /// <summary>
        /// This property gets all the Upstream Nodes  for a given node, ONLY after the graph is loaded. 
        /// This property is computed in ComputeUpstreamOnDownstreamNodes function
        /// </summary>
        internal HashSet<NodeModel> UpstreamCache = new HashSet<NodeModel>();

        #endregion

        #region events

        //TODO(Steve): Model should not have to worry about UI thread synchronization -- MAGN-5709

        /// <summary>
        ///     Called by nodes for behavior that they want to dispatch on the UI thread
        ///     Triggers event to be received by the UI. If no UI exists, behavior will not be executed.
        /// </summary>
        /// <param name="a"></param>
        public void DispatchOnUIThread(Action a)
        {
            OnDispatchedToUI(this, new UIDispatcherEventArgs(a));
        }

        private void OnDispatchedToUI(object sender, UIDispatcherEventArgs e)
        {
            if (DispatchedToUI != null)
                DispatchedToUI(sender, e);
        }

        internal event DispatchedToUIThreadHandler DispatchedToUI;

        /// <summary>
        /// Event triggered when a port is connected.
        /// </summary>
        public event Action<PortModel, ConnectorModel> PortConnected;

        /// <summary>
        /// Event triggered when a port is disconnected.
        /// </summary>
        public event Action<PortModel> PortDisconnected;

        #endregion

        #region public properties

        /// <summary>
        ///     Definitions for the Input Ports of this NodeModel.
        /// </summary>
        [Obsolete("InPortData is deprecated, please use the InPortNamesAttribute, InPortDescriptionsAttribute, and InPortTypesAttribute instead.")]
        public ObservableCollection<PortData> InPortData { get; private set; }

        /// <summary>
        ///     Definitions for the Output Ports of this NodeModel.
        /// </summary>
        [Obsolete("OutPortData is deprecated, please use the OutPortNamesAttribute, OutPortDescriptionsAttribute, and OutPortTypesAttribute instead.")]
        public ObservableCollection<PortData> OutPortData { get; private set; }

        /// <summary>
        ///     All of the connectors entering and exiting the NodeModel.
        /// </summary>
        public IEnumerable<ConnectorModel> AllConnectors
        {
            get
            {
                return inPorts.Concat(outPorts).SelectMany(port => port.Connectors);
            }
        }

        /// <summary>
        ///     Returns whether this node represents a built-in or custom function.
        /// </summary>
        public bool IsCustomFunction
        {
            get { return this is Function; }
        }

        /// <summary>
        ///     Returns whether the node is to be included in visualizations.
        /// </summary>
        public bool IsVisible
        {
            get
            {
                return isVisible;
            }

            private set // Private setter, see "ArgumentLacing" for details.
            {
                if (isVisible != value)
                {
                    isVisible = value;
                    RaisePropertyChanged("IsVisible");
                }
            }
        }

        /// <summary>
        ///     Returns whether the node aggregates its upstream connections
        ///     for visualizations.
        /// </summary>
        public bool IsUpstreamVisible
        {
            get
            {
                return isUpstreamVisible;
            }

            private set // Private setter, see "ArgumentLacing" for details.
            {
                if (isUpstreamVisible != value)
                {
                    isUpstreamVisible = value;
                    RaisePropertyChanged("IsUpstreamVisible");
                }
            }
        }

        /// <summary>
        /// Input nodes are used in Customizer and Presets. Input nodes can be numbers, number sliders,
        /// strings, bool, code blocks and custom nodes, which don't specify path. This property 
        /// is true for nodes that are potential inputs for Customizers and Presets.
        /// </summary>
        public virtual bool IsInputNode
        {
            get
            {
                return !inPorts.Any();
            }
        }

        private bool isSetAsInput = true;
        /// <summary>
        /// This property is user-controllable via a checkbox and is set to true when a user wishes to include
        /// this node in a Customizer as an interactive control.
        /// </summary>
        public bool IsSetAsInput
        {
            get
            {
                if (!IsInputNode)
                    return false;

                return isSetAsInput;
            }

            set
            {
                isSetAsInput = value;
            }
        }

        /// <summary>
        ///     The Node's state, which determines the coloring of the Node in the canvas.
        /// </summary>
        public ElementState State
        {
            get { return state; }
            set
            {
                if (value != ElementState.Error && value != ElementState.AstBuildBroken)
                    ClearTooltipText();

                // Check before settings and raising 
                // a notification.
                if (state == value) return;

                state = value;
                RaisePropertyChanged("State");
            }
        }

        /// <summary>
        ///   If the state of node is Error or AstBuildBroken
        /// </summary>
        public bool IsInErrorState
        {
            get
            {
                return state == ElementState.AstBuildBroken || state == ElementState.Error;
            }
        }

        /// <summary>
        ///     Text that is displayed as this Node's tooltip.
        /// </summary>
        public string ToolTipText
        {
            get { return toolTipText; }
            set
            {
                toolTipText = value;
                RaisePropertyChanged("ToolTipText");
            }
        }

        /// <summary>
        ///     Should we override the displayed name with this Node's NickName property?
        /// </summary>
        public bool OverrideNameWithNickName
        {
            get { return overrideNameWithNickName; }
            set
            {
                overrideNameWithNickName = value;
                RaisePropertyChanged("OverrideNameWithNickName");
            }
        }

        /// <summary>
        ///     The name that is displayed in the UI for this NodeModel.
        /// </summary>
        public string NickName
        {
            get { return nickName; }
            set
            {
                nickName = value;
                RaisePropertyChanged("NickName");
            }
        }

        /// <summary>
        ///     Collection of PortModels representing all Input ports.
        /// </summary>
        public ObservableCollection<PortModel> InPorts
        {
            get { return inPorts; }
            set
            {
                inPorts = value;
                RaisePropertyChanged("InPorts");
            }
        }

        /// <summary>
        ///     Collection of PortModels representing all Output ports.
        /// </summary>
        public ObservableCollection<PortModel> OutPorts
        {
            get { return outPorts; }
            set
            {
                outPorts = value;
                RaisePropertyChanged("OutPorts");
            }
        }

        public IDictionary<int, Tuple<int, NodeModel>> InputNodes
        {
            get { return inputNodes; }
        }

        public IDictionary<int, HashSet<Tuple<int, NodeModel>>> OutputNodes
        {
            get { return outputNodes; }
        }

        /// <summary>
        ///     Control how arguments lists of various sizes are laced.
        /// </summary>
        public LacingStrategy ArgumentLacing
        {
            get
            {
                return argumentLacing;
            }

            // The property setter is marked as private/protected because it 
            // should not be set from an external component directly. The ability
            // to directly set the property value causes a NodeModel to be altered 
            // without careful consideration of undo/redo recording. If changing 
            // this property value should be undo-able, then the caller should use 
            // "DynamoModel.UpdateModelValueCommand" to set the property value. 
            // The command ensures changes to the NodeModel is recorded for undo.
            // 
            // In some cases being able to set the property value directly is 
            // desirable, for example, some unit test scenarios require the given 
            // NodeModel property to be of certain value. In such cases the 
            // easiest workaround is to use "NodeModel.UpdateValue" method:
            // 
            //      someNode.UpdateValue("ArgumentLacing", "CrossProduct");
            // 
            protected set
            {
                if (argumentLacing != value)
                {
                    argumentLacing = value;
                    RaisePropertyChanged("ArgumentLacing");

                    // Mark node for update
                    OnNodeModified();
                }
            }
        }

        /// <summary>
        ///     Name property
        /// </summary>
        /// <value>
        ///     If the node has a name attribute, return it.  Otherwise return empty string.
        /// </value>
        public string Name
        {
            get
            {
                Type type = GetType();
                object[] attribs = type.GetCustomAttributes(typeof(NodeNameAttribute), false);
                if (type.Namespace == "Dynamo.Graph.Nodes" && !type.IsAbstract && attribs.Length > 0
                    && type.IsSubclassOf(typeof(NodeModel)))
                {
                    var elCatAttrib = attribs[0] as NodeNameAttribute;
                    return elCatAttrib.Name;
                }
                return "";
            }
        }

        /// <summary>
        ///     Category property
        /// </summary>
        /// <value>
        ///     If the node has a category, return it.  Other wise return empty string.
        /// </value>
        public string Category
        {
            get
            {
                category = category ?? GetCategoryStringFromAttributes();
                return category;
            }
            set
            {
                category = value;
                RaisePropertyChanged("Category");
            }
        }

        private string category;

        private string GetCategoryStringFromAttributes()
        {
            Type type = GetType();
            object[] attribs = type.GetCustomAttributes(typeof(NodeCategoryAttribute), false);

            if (type.Namespace != "Dynamo.Graph.Nodes" || type.IsAbstract || attribs.Length <= 0
                || !type.IsSubclassOf(typeof(NodeModel)))
                return "";

            var elCatAttrib = attribs[0] as NodeCategoryAttribute;
            return elCatAttrib.ElementCategory;
        }

        /// <summary>
        /// The value of this node after the most recent computation
        /// 
        /// As this property could be modified by the virtual machine, it's dangerous 
        /// to access this value without using the active Scheduler. Use the Scheduler to 
        /// remove the possibility of race conditions.
        /// </summary>
        public MirrorData CachedValue
        {
            get
            {
                lock (cachedValueMutex)
                {
                    return cachedValue;
                }
            }
            private set
            {
                lock (cachedValueMutex)
                {
                    cachedValue = value;
                }

                RaisePropertyChanged("CachedValue");
            }
        }

        /// <summary>
        /// WARNING: This method is meant for unit test only. It directly accesses
        /// the EngineController for the mirror data without waiting for any 
        /// possible execution to complete (which, in single-threaded nature of 
        /// unit test, is an okay thing to do). The right way to get the cached 
        /// value for a NodeModel is by going through its RequestValueUpdateAsync
        /// method).
        /// </summary>
        /// <param name="engine">Instance of EngineController from which the node
        /// value is to be retrieved.</param>
        /// <returns>Returns the MirrorData if the node's value is computed, or 
        /// null otherwise.</returns>
        /// 
        internal MirrorData GetCachedValueFromEngine(EngineController engine)
        {
            if (cachedValue != null)
                return cachedValue;

            // Do not have an identifier for preview right now. For an example,
            // this can be happening at the beginning of a code block node creation.
            if (AstIdentifierForPreview.Value == null)
                return null;

            cachedValue = null;

            var runtimeMirror = engine.GetMirror(AstIdentifierForPreview.Value);

            if (runtimeMirror != null)
                cachedValue = runtimeMirror.GetData();

            return cachedValue;
        }

        /// <summary>
        /// This flag is used to determine if a node was involved in a recent execution.
        /// The primary purpose of this flag is to determine if the node's render packages 
        /// should be returned to client browser when it requests for them. This is mainly 
        /// to avoid returning redundant data that has not changed during an execution.
        /// </summary>
        internal bool WasInvolvedInExecution { get; set; }

        /// <summary>
        /// This flag indicates if render packages of a NodeModel has been updated 
        /// since the last execution. UpdateRenderPackageAsyncTask will always be 
        /// generated for a NodeModel that took part in the evaluation, if this flag 
        /// is false.
        /// </summary>
        internal bool WasRenderPackageUpdatedAfterExecution { get; set; }

        /// <summary>
        ///     Search tags for this Node.
        /// </summary>
        public List<string> Tags
        {
            get
            {
                return
                    GetType()
                        .GetCustomAttributes(typeof(NodeSearchTagsAttribute), true)
                        .Cast<NodeSearchTagsAttribute>()
                        .SelectMany(x => x.Tags)
                        .ToList();
            }
        }

        /// <summary>
        ///     Description of this Node.
        /// </summary>
        public string Description
        {
            get
            {
                description = description ?? GetDescriptionStringFromAttributes();
                return description;
            }
            set
            {
                description = value;
                RaisePropertyChanged("Description");
            }
        }

        public bool CanUpdatePeriodically
        {
            get { return canUpdatePeriodically; }
            set
            {
                canUpdatePeriodically = value;
                RaisePropertyChanged("CanUpdatePeriodically");
            }
        }

        /// <summary>
        ///     ProtoAST Identifier for result of the node before any output unpacking has taken place.
        ///     If there is only one output for the node, this is equivalent to GetAstIdentifierForOutputIndex(0).
        /// </summary>
        public IdentifierNode AstIdentifierForPreview
        {
            get { return AstFactory.BuildIdentifier(AstIdentifierBase); }
        }

        /// <summary>
        ///     If this node is allowed to be converted to AST node in nodes to code conversion.
        /// </summary>
        public virtual bool IsConvertible
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        ///     Return a variable whose value will be displayed in preview window.
        ///     Derived nodes may overwrite this function to display default value
        ///     of this node. E.g., code block node may want to display the value
        ///     of the left hand side variable of last statement.
        /// </summary>
        public virtual string AstIdentifierBase
        {
            get
            {
                return AstBuilder.StringConstants.VarPrefix
                    + GUID.ToString().Replace("-", string.Empty);
            }
        }

        /// <summary>
        ///     Enable or disable label display. Default is false.
        /// </summary>
        public bool DisplayLabels
        {
            get { return displayLabels; }
            set
            {
                if (displayLabels == value)
                    return;

                displayLabels = value;
                RaisePropertyChanged("DisplayLabels");
            }
        }

        /// <summary>
        ///     Is this node being applied partially, resulting in a partial function?
        /// </summary>
        public bool IsPartiallyApplied //TODO(Steve): Move to Graph level -- MAGN-5710
        {
            get { return !Enumerable.Range(0, InPorts.Count).All(HasInput); }
        }

        /// <summary>
        ///     Get the description from type information
        /// </summary>
        /// <returns>The value or "No description provided"</returns>
        public string GetDescriptionStringFromAttributes()
        {
            Type t = GetType();
            object[] rtAttribs = t.GetCustomAttributes(typeof(NodeDescriptionAttribute), true);
            return rtAttribs.Length > 0
                ? ((NodeDescriptionAttribute)rtAttribs[0]).ElementDescription
                : Properties.Resources.NoDescriptionAvailable;
        }

        /// <summary>
        ///     Fetches the ProtoAST Identifier for a given output port.
        /// </summary>
        /// <param name="outputIndex">Index of the output port.</param>
        /// <returns>Identifier corresponding to the given output port.</returns>
        public virtual IdentifierNode GetAstIdentifierForOutputIndex(int outputIndex)
        {
            if (outputIndex < 0 || outputIndex > OutPorts.Count)
                throw new ArgumentOutOfRangeException("outputIndex", @"Index must correspond to an OutPortData index.");

            if (OutPorts.Count <= 1)
                return AstIdentifierForPreview;
            else
            {
                string id = AstIdentifierBase + "_out" + outputIndex;
                return AstFactory.BuildIdentifier(id);
            }
        }

        /// <summary>
        ///      The possible type of output at specified port. This 
        ///      type information is not necessary to be accurate.
        /// </summary>
        /// <returns></returns>
        public virtual ProtoCore.Type GetTypeHintForOutput(int index)
        {
            return ProtoCore.TypeSystem.BuildPrimitiveTypeObject(ProtoCore.PrimitiveType.kTypeVar);
        }
    
        /// <summary>
        /// A flag indicating whether the node is frozen.
        /// When a node is frozen, the node, and all nodes downstream will not participate in execution.
        /// This will return true if any upstream node is frozen or if the node was explicitly frozen.        
        /// </summary>
        /// <value>
        ///   <c>true</c> if this node is frozen; otherwise, <c>false</c>.
        /// </value>
        public bool IsFrozen
        {
            get
            {
                return IsAnyUpstreamFrozen() || isFrozenExplicitly;
            }
            set
            {
                isFrozenExplicitly = value;
                //If the node is Unfreezed then Mark all the downstream nodes as
                // modified. This is essential recompiling the AST.
                if (!value)
                { 
                    MarkDownStreamNodesAsModified(this);
                    OnNodeModified();  
                    RaisePropertyChanged("IsFrozen");
                }
                //If the node is frozen, then do not execute the graph immediately.
                // delete the node and its downstream nodes from AST.
                else
                {
                    ComputeUpstreamOnDownstreamNodes();
                    OnUpdateASTCollection();                  
                }
            }
        }

        #endregion

        #region freeze execution
        /// <summary>
        /// Determines whether any of the upstream node is frozen.
        /// </summary>
        /// <returns></returns>
        internal bool IsAnyUpstreamFrozen()
        {            
            return UpstreamCache.Any(x => x.isFrozenExplicitly);
        }

        /// <summary>
        /// For a given node, this function computes all the upstream nodes
        /// by gathering the cached upstream nodes on this node's immediate parents.
        /// </summary>
        internal void ComputeUpstreamCache()
        {
            this.UpstreamCache = new HashSet<NodeModel>();
            var inpNodes = this.InputNodes.Values;

            foreach (var inputnode in inpNodes.Where(x => x != null))
            {
                this.UpstreamCache.Add(inputnode.Item2);
                foreach (var upstreamNode in inputnode.Item2.UpstreamCache)
                {
                    this.UpstreamCache.Add(upstreamNode);
                }
            }
        }

        /// <summary>
        /// For a given node, this function computes all the upstream nodes
        /// by gathering the cached upstream nodes on this node's immediate parents.
        /// If a node has any downstream nodes, then for all those downstream nodes, upstream
        /// nodes will be computed. Essentially this method propogates the UpstreamCache down.
        /// Also this function gets called only after the workspace is added.    
        /// </summary>      
        internal void ComputeUpstreamOnDownstreamNodes()
        {
            //first compute upstream nodes for this node
            ComputeUpstreamCache();

            //then for downstream nodes
            //gather downstream nodes and bail if we see an already visited node
            HashSet<NodeModel> downStreamNodes = new HashSet<NodeModel>();
            this.GetDownstreamNodes(this, downStreamNodes);

            foreach (var downstreamNode in AstBuilder.TopologicalSort(downStreamNodes))
            {
                downstreamNode.UpstreamCache = new HashSet<NodeModel>();
                var currentinpNodes = downstreamNode.InputNodes.Values;                
                foreach (var inputnode in currentinpNodes.Where(x => x != null))
                {
                    downstreamNode.UpstreamCache.Add(inputnode.Item2);
                    foreach (var upstreamNode in inputnode.Item2.UpstreamCache)
                    {
                        downstreamNode.UpstreamCache.Add(upstreamNode);
                    }
                }
            }
                    
            RaisePropertyChanged("IsFrozen");           
        }
       
        private void MarkDownStreamNodesAsModified(NodeModel node)
        {
            HashSet<NodeModel> gathered = new HashSet<NodeModel>();
            GetDownstreamNodes(node, gathered);
            foreach (var iNode in gathered)
            {
                iNode.executionHint = ExecutionHints.Modified;
            }
        }

        /// <summary>
        /// Gets the downstream nodes for the given node.
        /// </summary>
        /// <param name="node">The node.</param>
        /// <param name="gathered">The gathered.</param>
        internal void GetDownstreamNodes(NodeModel node, HashSet<NodeModel> gathered)
        {
            if (gathered.Contains(node)) // Considered this node before, bail.pu
                return;

            gathered.Add(node);

            var sets = node.OutputNodes.Values;
            var outputNodes = sets.SelectMany(set => set.Select(t => t.Item2));
            foreach (var outputNode in outputNodes)
            {
                // Recursively get all downstream nodes.
                GetDownstreamNodes(outputNode, gathered);
            }
        }
        #endregion

        protected NodeModel()
        {
            InPortData = new ObservableCollection<PortData>();
            OutPortData = new ObservableCollection<PortData>();

            inputNodes = new Dictionary<int, Tuple<int, NodeModel>>();
            outputNodes = new Dictionary<int, HashSet<Tuple<int, NodeModel>>>();

            IsVisible = true;
            IsUpstreamVisible = true;
            ShouldDisplayPreviewCore = true;
            executionHint = ExecutionHints.Modified;

            PropertyChanged += delegate(object sender, PropertyChangedEventArgs args)
            {
                switch (args.PropertyName)
                {
                    case ("OverrideName"):
                        RaisePropertyChanged("NickName");
                        break;
                }
            };

            //Register port connection events.
            PortConnected += OnPortConnected;
            PortDisconnected += OnPortDisconnected;

            //Fetch the element name from the custom attribute.
            SetNickNameFromAttribute();

            IsSelected = false;
            State = ElementState.Dead;
            ArgumentLacing = LacingStrategy.Disabled;

            RaisesModificationEvents = true;
        }

        /// <summary>
        ///     Gets the most recent value of this node stored in an EngineController that has evaluated it.
        /// </summary>
        /// <param name="outPortIndex"></param>
        /// <param name="engine"></param>
        /// <returns></returns>
        public MirrorData GetValue(int outPortIndex, EngineController engine)
        {
            return engine.GetMirror(GetAstIdentifierForOutputIndex(outPortIndex).Value).GetData();
        }

        /// <summary>
        ///     Sets the nickname of this node from the attributes on the class definining it.
        /// </summary>
        public void SetNickNameFromAttribute()
        {
            var elNameAttrib = GetType().GetCustomAttributes<NodeNameAttribute>(false).FirstOrDefault();
            if (elNameAttrib != null)
                NickName = elNameAttrib.Name;

        }

        #region Modification Reporting

        /// <summary>
        ///     Indicate if the node should respond to NodeModified event. It
        ///     always should be true, unless is temporarily set to false to 
        ///     avoid flood of Modified event. 
        /// </summary>
        public bool RaisesModificationEvents { get; set; }

        /// <summary>
        ///     Event fired when the node's DesignScript AST should be recompiled
        /// </summary>
        public event Action<NodeModel> Modified;
        public virtual void OnNodeModified(bool forceExecute = false)
        {
            if (!RaisesModificationEvents || IsFrozen)
                return;
           
            MarkNodeAsModified(forceExecute);
            var handler = Modified;
            if (handler != null) handler(this);
        }

        /// <summary>
        /// Event fired when the node's DesignScript AST should be updated.
        /// This event deletes the frozen nodes from AST collection.
        /// </summary>
        public event Action<NodeModel> UpdateASTCollection;
        public virtual void OnUpdateASTCollection()
        {
            var handler = UpdateASTCollection;
            if (handler != null) handler(this);
        }

        /// <summary>
        /// Called when a node is requesting that the workspace's node modified events be
        /// silenced. This is particularly critical for code block nodes, whose modification can 
        /// mutate the workspace.
        /// 
        /// As opposed to RaisesModificationEvents, this modifies the entire parent workspace
        /// </summary>
        internal event Action<NodeModel, bool> RequestSilenceNodeModifiedEvents;

        internal void OnRequestSilenceModifiedEvents(bool silence)
        {
            if (RequestSilenceNodeModifiedEvents != null)
            {
                RequestSilenceNodeModifiedEvents(this, silence);
            }
        }

        #endregion

        #region ProtoAST Compilation

        /// <summary>
        /// Override this to declare the outputs for each of this Node's output ports.
        /// </summary>
        /// <param name="inputAstNodes">Ast for inputs indexed by input port index.</param>
        /// <returns>Sequence of AssociativeNodes representing this Node's code output.</returns>
        public virtual IEnumerable<AssociativeNode> BuildOutputAst(List<AssociativeNode> inputAstNodes)
        {
            return
                OutPortData.Enumerate()
                           .Select(
                               output => AstFactory.BuildAssignment(
                                   GetAstIdentifierForOutputIndex(output.Index),
                                   new NullNode()));
        }

        /// <summary>
        /// Wraps the publically overrideable `BuildOutputAst` method so that it works with Preview.
        /// </summary>
        /// <param name="inputAstNodes"></param>
        /// <param name="context">Compilation context</param>
        internal virtual IEnumerable<AssociativeNode> BuildAst(List<AssociativeNode> inputAstNodes, CompilationContext context)
        {
            OnBuilt();

            IEnumerable<AssociativeNode> result = null;

            try
            {
                result = BuildOutputAst(inputAstNodes);
            }
            catch (Exception e)
            {
                // If any exception from BuildOutputAst(), we emit
                // a function call "var_guid = %nodeAstFailed(full.node.name)"
                // for this node, set the state of node to AstBuildBroken and
                // disply the corresponding error message. 
                // 
                // The return value of function %nodeAstFailed() is always 
                // null.
                var errorMsg = Properties.Resources.NodeProblemEncountered;
                var fullMsg = errorMsg + "\n\n" + e.Message;
                this.NotifyAstBuildBroken(fullMsg);

                var fullName = this.GetType().ToString();
                var astNodeFullName = AstFactory.BuildStringNode(fullName);
                var arguments = new List<AssociativeNode> { astNodeFullName };
                var func = AstFactory.BuildFunctionCall(Constants.kNodeAstFailed, arguments);

                return new[]
                {
                    AstFactory.BuildAssignment(AstIdentifierForPreview, func)
                };
            }

            if (OutPorts.Count == 1)
            {
                var firstOuputIdent = GetAstIdentifierForOutputIndex(0);
                if (!AstIdentifierForPreview.Equals(firstOuputIdent))
                {
                    result = result.Concat(
                    new[]
                    {
                        AstFactory.BuildAssignment(AstIdentifierForPreview, firstOuputIdent)
                    });
                }
                return result;
            }

            var emptyList = AstFactory.BuildExprList(new List<AssociativeNode>());
            var previewIdInit = AstFactory.BuildAssignment(AstIdentifierForPreview, emptyList);

            return
                result.Concat(new[] { previewIdInit })
                      .Concat(
                          OutPortData.Select(
                              (outNode, index) =>
                                  AstFactory.BuildAssignment(
                                      new IdentifierNode(AstIdentifierForPreview)
                                      {
                                          ArrayDimensions =
                                              new ArrayNode
                                              {
                                                  Expr = new StringNode { Value = outNode.NickName }
                                              }
                                      },
                                      GetAstIdentifierForOutputIndex(index))));
        }

        /// <summary>
        ///     Callback for when this NodeModel has been compiled.
        /// </summary>
        protected virtual void OnBuilt()
        {
        }

        /// <summary>
        /// Apppend replication guide to the input parameter based on lacing
        /// strategy.
        /// </summary>
        /// <param name="inputs"></param>
        /// <returns></returns>
        public void AppendReplicationGuides(List<AssociativeNode> inputs)
        {
            if (inputs == null || !inputs.Any())
                return;

            switch (ArgumentLacing)
            {
                case LacingStrategy.Longest:

                    for (int i = 0; i < inputs.Count(); ++i)
                    {
                        inputs[i] = AstFactory.AddReplicationGuide(
                                                inputs[i],
                                                new List<int> { 1 },
                                                true);
                    }
                    break;

                case LacingStrategy.CrossProduct:

                    int guide = 1;
                    for (int i = 0; i < inputs.Count(); ++i)
                    {
                        inputs[i] = AstFactory.AddReplicationGuide(
                                                inputs[i],
                                                new List<int> { guide },
                                                false);
                        guide++;
                    }
                    break;
            }
        }
        #endregion

        #region Input and Output Connections

        /// <summary>
        ///     Event fired when a new ConnectorModel has been attached to one of this node's inputs.
        /// </summary>
        public event Action<ConnectorModel> ConnectorAdded;
        protected virtual void OnConnectorAdded(ConnectorModel obj)
        {
            var handler = ConnectorAdded;
            if (handler != null) handler(obj);
        }

        /// <summary>
        /// If node is connected to some other node(other than Output) then it is not a 'top' node
        /// </summary>
        public bool IsTopMostNode
        {
            get
            {
                if (OutPortData.Count < 1)
                    return false;

                foreach (var port in OutPorts.Where(port => port.Connectors.Count != 0))
                {
                    return port.Connectors.Any(connector => connector.End.Owner is Output);
                }

                return true;
            }
        }

        internal void ConnectInput(int inputData, int outputData, NodeModel node)
        {
            inputNodes[inputData] = Tuple.Create(outputData, node);
        }

        internal void ConnectOutput(int portData, int inputData, NodeModel nodeLogic)
        {
            if (!outputNodes.ContainsKey(portData))
                outputNodes[portData] = new HashSet<Tuple<int, NodeModel>>();
            outputNodes[portData].Add(Tuple.Create(inputData, nodeLogic));
        }

        internal void DisconnectInput(int data)
        {
            inputNodes[data] = null;
        }

        /// <summary>
        ///     Attempts to get the input for a certain port.
        /// </summary>
        /// <param name="data">PortData to look for an input for.</param>
        /// <param name="input">If an input is found, it will be assigned.</param>
        /// <returns>True if there is an input, false otherwise.</returns>
        public bool TryGetInput(int data, out Tuple<int, NodeModel> input)
        {
            return inputNodes.TryGetValue(data, out input) && input != null;
        }

        /// <summary>
        ///     Attempts to get the output for a certain port.
        /// </summary>
        /// <param name="output">Index to look for an output for.</param>
        /// <param name="newOutputs">If an output is found, it will be assigned.</param>
        /// <returns>True if there is an output, false otherwise.</returns>
        public bool TryGetOutput(int output, out HashSet<Tuple<int, NodeModel>> newOutputs)
        {
            return outputNodes.TryGetValue(output, out newOutputs);
        }

        /// <summary>
        ///     Checks if there is an input for a certain port.
        /// </summary>
        /// <param name="data">Index of the port to look for an input for.</param>
        /// <returns>True if there is an input, false otherwise.</returns>
        public bool HasInput(int data)
        {
            return HasConnectedInput(data) || (InPorts.Count > data && InPorts[data].UsingDefaultValue);
        }

        /// <summary>
        ///     Checks if there is a connected input for a certain port. This does
        ///     not count default values as an input.
        /// </summary>
        /// <param name="data">Index of the port to look for an input for.</param>
        /// <returns>True if there is an input, false otherwise.</returns>
        public bool HasConnectedInput(int data)
        {
            return inputNodes.ContainsKey(data) && inputNodes[data] != null;
        }

        /// <summary>
        ///     Checks if there is an output for a certain port.
        /// </summary>
        /// <param name="portData">Index of the port to look for an output for.</param>
        /// <returns>True if there is an output, false otherwise.</returns>
        public bool HasOutput(int portData)
        {
            return outputNodes.ContainsKey(portData) && outputNodes[portData].Any();
        }

        internal void DisconnectOutput(int portData, int inPortData, NodeModel nodeModel)
        {
            HashSet<Tuple<int, NodeModel>> output;
            if (outputNodes.TryGetValue(portData, out output))
                output.RemoveWhere(x => x.Item2 == nodeModel && x.Item1 == inPortData);
        }

        #endregion

        #region UI Framework

        private void ClearTooltipText()
        {
            ToolTipText = "";
        }

        /// <summary>
        /// Clears the errors/warnings that are generated when running the graph.
        /// If the node has a value supplied for the persistentWarning, then the
        /// node's State will be set to ElementState.Persistent and the ToolTipText will
        /// be set to the persistent warning. Otherwise, the State will be 
        /// set to ElementState.Dead
        /// </summary>
        public virtual void ClearRuntimeError()
        {
            if (!string.IsNullOrEmpty(persistentWarning))
            {
                State = ElementState.PersistentWarning;
                ToolTipText = persistentWarning;
            }
            else
            {
                State = ElementState.Dead;
                ClearTooltipText();
            }

            ValidateConnections();
        }

        public void SelectNeighbors()
        {
            IEnumerable<ConnectorModel> outConnectors = outPorts.SelectMany(x => x.Connectors);
            IEnumerable<ConnectorModel> inConnectors = inPorts.SelectMany(x => x.Connectors);

            foreach (var c in outConnectors.Where(c => !DynamoSelection.Instance.Selection.Contains(c.End.Owner)))
                DynamoSelection.Instance.Selection.Add(c.End.Owner);

            foreach (var c in inConnectors.Where(c => !DynamoSelection.Instance.Selection.Contains(c.Start.Owner)))
                DynamoSelection.Instance.Selection.Add(c.Start.Owner);
        }

        #region Node State

        public void ValidateConnections()
        {
            // if there are inputs without connections
            // mark as dead; otherwise, if the original state is dead,
            // update it as active.
            if (inPorts.Any(x => !x.Connectors.Any() && !(x.UsingDefaultValue && x.DefaultValueEnabled)))
            {
                if (State == ElementState.Active)
                {
                    State = string.IsNullOrEmpty(persistentWarning)
                        ? ElementState.Dead
                        : ElementState.PersistentWarning;
                }
            }
            else
            {
                if (State == ElementState.Dead)
                {
                    State = string.IsNullOrEmpty(persistentWarning)
                        ? ElementState.Active
                        : ElementState.PersistentWarning;
                }
            }
        }

        public void Error(string p)
        {
            State = ElementState.Error;
            ToolTipText = p;
        }

        /// <summary>
        /// Set a warning on a node. 
        /// </summary>
        /// <param name="p">The warning text.</param>
        /// <param name="isPersistent">Is the warning persistent? If true, the warning will not be
        /// cleared when the node is next evaluated and any additional warning messages will be concatenated
        /// to the persistent error message. If false, the warning will be cleared on the next evaluation.</param>
        public void Warning(string p, bool isPersistent = false)
        {
            if (isPersistent)
            {
                State = ElementState.PersistentWarning;
                ToolTipText = string.Format("{0}\n{1}", persistentWarning, p);
            }
            else
            {
                State = ElementState.Warning;
                ToolTipText = p;
            }
        }

        /// <summary>
        /// Change the state of node to ElementState.AstBuildBroken and display
        /// "p" in tooltip window. 
        /// </summary>
        /// <param name="p"></param>
        public void NotifyAstBuildBroken(string p)
        {
            State = ElementState.AstBuildBroken;
            ToolTipText = p;
        }

        #endregion

        #region Port Management

        internal int GetPortModelIndex(PortModel portModel)
        {
            if (portModel.PortType == PortType.Input)
                return InPorts.IndexOf(portModel);
            else
                return OutPorts.IndexOf(portModel);
        }

        /// <summary>
        /// If a "PortModel.LineIndex" property isn't "-1", then it is a PortModel
        /// meant to match up with a line in code block node. A code block node may 
        /// contain empty lines in it, resulting in one PortModel being spaced out 
        /// from another one. In such cases, the vertical position of PortModel is 
        /// dependent of its "LineIndex".
        /// 
        /// If a "PortModel.LineIndex" property is "-1", then it is a regular 
        /// PortModel. Regular PortModel stacks up on one another with equal spacing,
        /// so their positions are based solely on "PortModel.Index".
        /// </summary>
        /// <param name="portModel">The portModel whose vertical offset is to be computed.</param>
        /// <returns>Returns the offset of the given port from the top of the ports</returns>
        //TODO(Steve): This kind of UI calculation should probably live on the VM. -- MAGN-5711
        internal double GetPortVerticalOffset(PortModel portModel)
        {
            double verticalOffset = 2.9;
            int index = portModel.LineIndex == -1 ? portModel.Index : portModel.LineIndex;

            //If the port was not found, then it should have just been deleted. Return from function
            if (index == -1)
                return verticalOffset;

            double portHeight = portModel.Height;
            return verticalOffset + index * portModel.Height;
        }

        /// <summary>
        ///     Reads inputs list and adds ports for each input.
        /// </summary>
        [Obsolete("RegisterInputPorts is deprecated, please use the InPortNamesAttribute, InPortDescriptionsAttribute, and InPortTypesAttribute instead.")]
        public void RegisterInputPorts()
        {
            RaisesModificationEvents = false;

            var inputs = new List<PortData>();

            // Old version of input ports registration.
            // Used InPortData.
            if (InPortData.Count > 0)
            {
                inputs.AddRange(InPortData);
            }

            // New version of input ports registration.
            // Used port Attributes.
            if (!areInputPortsRegistered)
            {
                inputs.AddRange(GetPortDataFromAttributes(PortType.Input));
            }

            //read the inputs list and create a number of
            //input ports
            int count = 0;
            foreach (PortData pd in inputs)
            {
                //add a port for each input
                //distribute the ports along the 
                //edges of the icon
                PortModel port = AddPort(PortType.Input, pd, count);
                //MVVM: AddPort now returns a port model. You can't set the data context here.
                //port.DataContext = this;

                portDataDict[port] = pd;
                count++;
            }

            if (inPorts.Count > count)
            {
                foreach (PortModel inport in inPorts.Skip(count))
                {
                    inport.DestroyConnectors();
                    portDataDict.Remove(inport);
                }

                for (int i = inPorts.Count - 1; i >= count; i--)
                    inPorts.RemoveAt(i);
            }

            //Configure Snap Edges
            ConfigureSnapEdges(inPorts);
            areInputPortsRegistered = true;

            RaisesModificationEvents = true;
            OnNodeModified();
        }

        /// <summary>
        ///     Reads outputs list and adds ports for each output
        /// </summary>
        [Obsolete("RegisterOutputPorts is deprecated, please use the OutPortNamesAttribute, OutPortDescriptionsAttribute, and OutPortTypesAttribute instead.")]
        public void RegisterOutputPorts()
        {
            RaisesModificationEvents = false;

            var outputs = new List<PortData>();

            // Old version of output ports registration.
            // Used OutPortData.
            if (OutPortData.Count > 0)
            {
                outputs.AddRange(OutPortData);
            }

            // New version of output ports registration.
            // Used port Attributes.
            if (!areOutputPortsRegistered)
            {
                outputs.AddRange(GetPortDataFromAttributes(PortType.Output));
            }

            //read the inputs list and create a number of
            //input ports
            int count = 0;
            foreach (PortData pd in outputs)
            {
                //add a port for each input
                //distribute the ports along the 
                //edges of the icon
                PortModel port = AddPort(PortType.Output, pd, count);

                //MVVM : don't set the data context in the model
                //port.DataContext = this;

                portDataDict[port] = pd;
                count++;
            }

            if (outPorts.Count > count)
            {
                foreach (PortModel outport in outPorts.Skip(count))
                    outport.DestroyConnectors();

                for (int i = outPorts.Count - 1; i >= count; i--)
                    outPorts.RemoveAt(i);

                //OutPorts.RemoveRange(count, outPorts.Count - count);
            }

            //configure snap edges
            ConfigureSnapEdges(outPorts);
            areOutputPortsRegistered = true;

            RaisesModificationEvents = true;
            OnNodeModified();
        }

        /// <summary>
        /// Tries to load ports names and descriptions from attributes.
        /// </summary>
        /// <param name="portType">Input or Output port type</param>
        private IEnumerable<PortData> GetPortDataFromAttributes(PortType portType)
        {
            var type = GetType();
            List<string> names = null;
            List<string> descriptions = null;

            switch (portType)
            {
                case PortType.Input:
                    {
                        names = type.GetCustomAttributes<InPortNamesAttribute>(false)
                                .SelectMany(x => x.PortNames)
                                .ToList();
                        descriptions = type.GetCustomAttributes<InPortDescriptionsAttribute>(false)
                            .SelectMany(x => x.PortDescriptions)
                            .ToList();
                        break;
                    }
                case PortType.Output:
                    {
                        names = type.GetCustomAttributes<OutPortNamesAttribute>(false)
                                .SelectMany(x => x.PortNames)
                                .ToList();
                        descriptions = type.GetCustomAttributes<OutPortDescriptionsAttribute>(false)
                            .SelectMany(x => x.PortDescriptions)
                            .ToList();
                        break;
                    }
            }

            if (names == null)
            {
                return new List<PortData>();
            }

            if (names.Count != descriptions.Count)
            {
                Log(String.Concat(
                        Name,
                        ": ",
                        Properties.Resources.PortsNameDescriptionDoNotEqualWarningMessage));

                // Take the same number of descriptions as number of names.
                descriptions = new List<string>(descriptions.Take(names.Count));
            }

            var ports = new List<PortData>();
            for (int i = 0; i < names.Count; i++)
            {
                string descr = i < descriptions.Count ? descriptions[i] : String.Empty;
                var pd = new PortData(names[i], descr);
                ports.Add(pd);
            }

            return ports;
        }

        /// <summary>
        /// Configures the snap edges.
        /// </summary>
        /// <param name="ports">The ports.</param>
        private static void ConfigureSnapEdges(IList<PortModel> ports)
        {
            switch (ports.Count)
            {
                case 0:
                    break;
                case 1:
                    ports[0].extensionEdges = SnapExtensionEdges.Top | SnapExtensionEdges.Bottom;
                    break;
                case 2:
                    ports[0].extensionEdges = SnapExtensionEdges.Top;
                    ports[1].extensionEdges = SnapExtensionEdges.Bottom;
                    break;
                default:
                    ports[0].extensionEdges = SnapExtensionEdges.Top;
                    ports[ports.Count - 1].extensionEdges = SnapExtensionEdges.Bottom;
                    var query =
                        ports.Where(
                            port => !port.extensionEdges.HasFlag(SnapExtensionEdges.Top | SnapExtensionEdges.Bottom)
                                && !port.extensionEdges.HasFlag(SnapExtensionEdges.Top)
                                && !port.extensionEdges.HasFlag(SnapExtensionEdges.Bottom));
                    foreach (var port in query)
                        port.extensionEdges = SnapExtensionEdges.None;
                    break;
            }
        }

        /// <summary>
        ///     Updates UI so that all ports reflect current state of node ports.
        /// </summary>
        public void RegisterAllPorts()
        {
            RegisterInputPorts();
            RegisterOutputPorts();
            ValidateConnections();
        }

        /// <summary>
        ///     Add a port to this node. If the port already exists, return that port.
        /// </summary>
        /// <param name="portType"></param>
        /// <param name="data"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public PortModel AddPort(PortType portType, PortData data, int index)
        {
            PortModel p;
            switch (portType)
            {
                case PortType.Input:
                    if (inPorts.Count > index)
                    {
                        p = inPorts[index];
                        p.SetPortData(data);
                    }
                    else
                    {
                        p = new PortModel(portType, this, data);

                        p.PropertyChanged += delegate(object sender, PropertyChangedEventArgs args)
                        {
                            if (args.PropertyName == "UsingDefaultValue")
                            {
                                OnNodeModified();
                            }
                        };
                        
                        InPorts.Add(p);
                    }

                    return p;

                case PortType.Output:
                    if (outPorts.Count > index)
                    {
                        p = outPorts[index];
                        p.SetPortData(data);
                    }
                    else
                    {
                        p = new PortModel(portType, this, data);
                        OutPorts.Add(p);
                    }

                    return p;
            }

            return null;
        }

        /// <summary>
        /// This method to be called by the ports to raise the PortConnected event.
        /// </summary>
        /// <param name="port"></param>
        /// <param name="connector"></param>
        internal void RaisePortConnectedEvent(PortModel port, ConnectorModel connector)
        {
            var handler = PortConnected;
            if (null != handler) handler(port, connector);
        }

        /// <summary>
        /// This method to be called by the ports to raise the PortDisconnected event.
        /// </summary>
        /// <param name="port"></param>
        internal void RaisePortDisconnectedEvent(PortModel port)
        {
            var handler = PortDisconnected;
            if (null != handler) handler(port);
        }

        private void OnPortConnected(PortModel port, ConnectorModel connector)
        {
            ValidateConnections();

            if (port.PortType != PortType.Input) return;

            var data = InPorts.IndexOf(port);
            var startPort = connector.Start;
            var outData = startPort.Owner.OutPorts.IndexOf(startPort);
            ConnectInput(data, outData, startPort.Owner);
            startPort.Owner.ConnectOutput(outData, data, this);
            OnConnectorAdded(connector);

            OnNodeModified();
        }

        private void OnPortDisconnected(PortModel port)
        {
            ValidateConnections();

            if (port.PortType != PortType.Input) return;

            var data = InPorts.IndexOf(port);
            var startPort = port.Connectors[0].Start;
            DisconnectInput(data);
            startPort.Owner.DisconnectOutput(startPort.Owner.OutPorts.IndexOf(startPort), data, this);

            OnNodeModified();
        }

        #endregion

        #endregion

        #region Code Serialization

        /// <summary>
        ///     Creates a Scheme representation of this dynNode and all connected dynNodes.
        /// </summary>
        /// <returns>S-Expression</returns>
        public virtual string PrintExpression()
        {
            string nick = NickName.Replace(' ', '_');

            if (!Enumerable.Range(0, InPorts.Count).Any(HasInput))
                return nick;

            string s = "";

            if (Enumerable.Range(0, InPorts.Count).All(HasInput))
            {
                s += "(" + nick;
                foreach (int data in Enumerable.Range(0, InPorts.Count))
                {
                    Tuple<int, NodeModel> input;
                    TryGetInput(data, out input);
                    s += " " + input.Item2.PrintExpression();
                }
                s += ")";
            }
            else
            {
                s += "(lambda (" + string.Join(" ", InPorts.Where((_, i) => !HasInput(i)).Select(x => x.PortName))
                     + ") (" + nick;
                foreach (int data in Enumerable.Range(0, InPorts.Count))
                {
                    s += " ";
                    Tuple<int, NodeModel> input;
                    if (TryGetInput(data, out input))
                        s += input.Item2.PrintExpression();
                    else
                        s += InPorts[data].PortName;
                }
                s += "))";
            }

            return s;
        }

        #endregion

        #region ISelectable Interface

        public override void Deselect()
        {
            ValidateConnections();
            IsSelected = false;
        }

        #endregion

        #region Command Framework Supporting Methods

        protected override bool UpdateValueCore(UpdateValueParams updateValueParams)
        {
            string name = updateValueParams.PropertyName;
            string value = updateValueParams.PropertyValue;

            switch (name)
            {
                case "NickName":
                    NickName = value;
                    return true;

                case "Position":
                    // Here we expect a string that represents an array of double values which are separated by ";"
                    // For example "12.5;14.56"
                    var pos = value.Split(';');
                    double xPos, yPos;
                    if (pos.Length == 2 && double.TryParse(pos[0], out xPos)
                        && double.TryParse(pos[1], out yPos))
                    {
                        X = xPos;
                        Y = yPos;
                        ReportPosition();
                    }

                    return true;

                case "UsingDefaultValue":
                    if (string.IsNullOrWhiteSpace(value))
                        return true;

                    // Here we expect a string that represents an array of Boolean values which are separated by ";"
                    var arr = value.Split(';');
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var useDef = !bool.Parse(arr[i]);
                        // do not set true, if default value is disabled
                        if (!useDef || InPorts[i].DefaultValueEnabled)
                        {
                            InPorts[i].UsingDefaultValue = useDef;
                        }
                    }
                    return true;

                case "ArgumentLacing":
                    LacingStrategy strategy;
                    if (!Enum.TryParse(value, out strategy))
                        strategy = LacingStrategy.Disabled;
                    ArgumentLacing = strategy;
                    return true;

                case "IsVisible":
                    bool newVisibilityValue;
                    if (bool.TryParse(value, out newVisibilityValue))
                        IsVisible = newVisibilityValue;
                    return true;

                case "IsUpstreamVisible":
                    bool newUpstreamVisibilityValue;
                    if (bool.TryParse(value, out newUpstreamVisibilityValue))
                        IsUpstreamVisible = newUpstreamVisibilityValue;
                    return true;

                case "IsFrozen":
                    bool newIsFrozen;
                    if (bool.TryParse(value, out newIsFrozen))
                    {
                        IsFrozen = newIsFrozen;
                    }
                    return true;
            }

            return base.UpdateValueCore(updateValueParams);
        }

        #endregion

        #region Serialization/Deserialization Methods

        /// <summary>
        ///     Called when the node's Workspace has been saved.
        /// </summary>
        protected internal virtual void OnSave() { }

        protected override void SerializeCore(XmlElement element, SaveContext context)
        {
            var helper = new XmlElementHelper(element);

            if (context != SaveContext.Copy)
                helper.SetAttribute("guid", GUID);

            // Set the type attribute
            helper.SetAttribute("type", GetType());
            helper.SetAttribute("nickname", NickName);
            helper.SetAttribute("x", X);
            helper.SetAttribute("y", Y);
            helper.SetAttribute("isVisible", IsVisible);
            helper.SetAttribute("isUpstreamVisible", IsUpstreamVisible);
            helper.SetAttribute("lacing", ArgumentLacing.ToString());
            helper.SetAttribute("isSelectedInput", IsSetAsInput.ToString());
            helper.SetAttribute("IsFrozen", isFrozenExplicitly);

            var portsWithDefaultValues =
                inPorts.Select((port, index) => new { port, index })
                   .Where(x => x.port.UsingDefaultValue);

            //write port information
            foreach (var port in portsWithDefaultValues)
            {
                XmlElement portInfo = element.OwnerDocument.CreateElement("PortInfo");
                portInfo.SetAttribute("index", port.index.ToString(CultureInfo.InvariantCulture));
                portInfo.SetAttribute("default", true.ToString());
                element.AppendChild(portInfo);
            }

            // Fix: MAGN-159 (nodes are not editable after undo/redo).
            if (context == SaveContext.Undo)
            {
                //helper.SetAttribute("interactionEnabled", interactionEnabled);
                helper.SetAttribute("nodeState", state.ToString());
            }

            if (context == SaveContext.File)
                OnSave();
        }

        protected override void DeserializeCore(XmlElement nodeElement, SaveContext context)
        {
            var helper = new XmlElementHelper(nodeElement);

            if (context != SaveContext.Copy)
                GUID = helper.ReadGuid("guid", GUID);

            // Resolve node nick name.
            string name = helper.ReadString("nickname", string.Empty);
            if (!string.IsNullOrEmpty(name))
                nickName = name;
            else
            {
                Type type = GetType();
                object[] attribs = type.GetCustomAttributes(typeof(NodeNameAttribute), true);
                var attrib = attribs[0] as NodeNameAttribute;
                if (null != attrib)
                    nickName = attrib.Name;
            }

            X = helper.ReadDouble("x", 0.0);
            Y = helper.ReadDouble("y", 0.0);
            isVisible = helper.ReadBoolean("isVisible", true);
            isUpstreamVisible = helper.ReadBoolean("isUpstreamVisible", true);
            argumentLacing = helper.ReadEnum("lacing", LacingStrategy.Disabled);
            IsSetAsInput = helper.ReadBoolean("isSelectedInput", true);
            isFrozenExplicitly = helper.ReadBoolean("IsFrozen", false);

            var portInfoProcessed = new HashSet<int>();

            //read port information
            foreach (XmlNode subNode in nodeElement.ChildNodes)
            {
                if (subNode.Name == "PortInfo")
                {
                    int index = int.Parse(subNode.Attributes["index"].Value);
                    if (index < InPorts.Count)
                    {
                        portInfoProcessed.Add(index);
                        bool def = bool.Parse(subNode.Attributes["default"].Value);
                        inPorts[index].UsingDefaultValue = def;
                    }
                }
            }

            //set defaults
            foreach (
                var port in
                    inPorts.Select((x, i) => new { x, i }).Where(x => !portInfoProcessed.Contains(x.i)))
                port.x.UsingDefaultValue = false;

            if (context == SaveContext.Undo)
            {
                // Fix: MAGN-159 (nodes are not editable after undo/redo).
                //interactionEnabled = helper.ReadBoolean("interactionEnabled", true);
                state = helper.ReadEnum("nodeState", ElementState.Active);

                // We only notify property changes in an undo/redo operation. Normal
                // operations like file loading or copy-paste have the models created
                // in different ways and their views will always be up-to-date with 
                // respect to their models.
                RaisePropertyChanged("InteractionEnabled");
                RaisePropertyChanged("State");
                RaisePropertyChanged("NickName");
                RaisePropertyChanged("ArgumentLacing");
                RaisePropertyChanged("IsVisible");
                RaisePropertyChanged("IsUpstreamVisible");    
            
                //we need to modify the downstream nodes manually in case the 
                //undo is for toggling freeze. This is ONLY modifying the execution hint.
                // this does not run the graph.
                RaisePropertyChanged("IsFrozen");
                MarkDownStreamNodesAsModified(this);
               
                // Notify listeners that the position of the node has changed,
                // then all connected connectors will also redraw themselves.
                ReportPosition();

            }
        }

        #endregion

        #region Dirty Management
        //TODO: Refactor Property into Automatic with private(?) setter
        //TODO: Add RequestRecalc() method to replace setter --steve

        /// <summary>
        /// Execution scenarios for a Node to be re-executed
        /// </summary>
        [Flags]
        protected enum ExecutionHints
        {
            None = 0,
            Modified = 1,       // Marks as modified, but execution is optional if AST is unchanged.
            ForceExecute = 3    // Marks as modified, force execution even if AST is unchanged.
        }

        private ExecutionHints executionHint;

        public bool IsModified
        {
            get { return GetExecutionHintsCore().HasFlag(ExecutionHints.Modified); }
        }

        public bool NeedsForceExecution
        {
            get { return GetExecutionHintsCore().HasFlag(ExecutionHints.ForceExecute); }
        }

        public void MarkNodeAsModified(bool forceExecute = false)
        {
            executionHint = ExecutionHints.Modified;

            if (forceExecute)
                executionHint |= ExecutionHints.ForceExecute;
        }

        public void ClearDirtyFlag()
        {
            executionHint = ExecutionHints.None;
        }

        protected virtual ExecutionHints GetExecutionHintsCore()
        {
            return executionHint;
        }
        #endregion

        #region Visualization Related Methods

        /// <summary>
        /// Call this method to asynchronously update the cached MirrorData for 
        /// this NodeModel through DynamoScheduler. AstIdentifierForPreview is 
        /// being accessed within this method, therefore the method is typically
        /// called from the main/UI thread.
        /// </summary>
        /// 
        internal void RequestValueUpdateAsync(IScheduler scheduler, EngineController engine)
        {
            // A NodeModel should have its cachedMirrorData reset when it is 
            // requested to update its value. When the QueryMirrorDataAsyncTask 
            // returns, it will update cachedMirrorData with the latest value.
            // 
            lock (cachedValueMutex)
            {
                cachedValue = null;
            }

            // Do not have an identifier for preview right now. For an example,
            // this can be happening at the beginning of a code block node creation.
            var variableName = AstIdentifierForPreview.Value;
            if (string.IsNullOrEmpty(variableName))
                return;

            var task = new QueryMirrorDataAsyncTask(new QueryMirrorDataParams
            {
                Scheduler = scheduler,
                EngineController = engine,
                VariableName = variableName
            });

            task.Completed += QueryMirrorDataAsyncTaskCompleted;
            scheduler.ScheduleForExecution(task);
        }

        private void QueryMirrorDataAsyncTaskCompleted(AsyncTask asyncTask)
        {
            asyncTask.Completed -= QueryMirrorDataAsyncTaskCompleted;

            var task = asyncTask as QueryMirrorDataAsyncTask;
            if (task == null)
            {
                throw new InvalidOperationException("Expected a " + typeof(QueryMirrorDataAsyncTask).Name
                    + ", but got a " + asyncTask.GetType().Name);
            }

            this.CachedValue = task.MirrorData;
        }

        /// <summary>
        /// Call this method to asynchronously regenerate render package for 
        /// this node. This method accesses core properties of a NodeModel and 
        /// therefore is typically called on the main/UI thread.
        /// </summary>
        /// <param name="scheduler">An IScheduler on which the task will be scheduled.</param>
        /// <param name="engine">The EngineController which will be used to get MirrorData for the node.</param>
        /// <param name="factory">An IRenderPackageFactory which will be used to generate IRenderPackage objects.</param>
        /// <param name="forceUpdate">Normally, render packages are only generated when the node's IsUpdated parameter is true.
        /// By setting forceUpdate to true, the render packages will be updated.</param>
        /// <returns>Flag which indicates if geometry update has been scheduled</returns>
        public virtual bool RequestVisualUpdateAsync(IScheduler scheduler,
            EngineController engine, IRenderPackageFactory factory, bool forceUpdate = false)
        {
            var initParams = new UpdateRenderPackageParams()
            {
                Node = this,
                RenderPackageFactory = factory,
                EngineController = engine,
                DrawableIds = GetDrawableIds(),
                PreviewIdentifierName = AstIdentifierForPreview.Name,
                ForceUpdate = forceUpdate
            };

            var task = new UpdateRenderPackageAsyncTask(scheduler);
            if (!task.Initialize(initParams)) return false;

            task.Completed += OnRenderPackageUpdateCompleted;
            scheduler.ScheduleForExecution(task);
            return true;
        }

        /// <summary>
        /// This event handler is invoked when UpdateRenderPackageAsyncTask is 
        /// completed, at which point the render packages (specific to this node) 
        /// become available. 
        /// </summary>
        /// <param name="asyncTask">The instance of UpdateRenderPackageAsyncTask
        /// that was responsible of generating the render packages.</param>
        /// 
        private void OnRenderPackageUpdateCompleted(AsyncTask asyncTask)
        {
            var task = asyncTask as UpdateRenderPackageAsyncTask;
            if (task.RenderPackages.Any())
            {
                var packages = new List<IRenderPackage>();

                packages.AddRange(task.RenderPackages);
                packages.AddRange(OnRequestRenderPackages());

                OnRenderPackagesUpdated(packages);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public event Func<IEnumerable<IRenderPackage>> RequestRenderPackages;

        /// <summary>
        /// This event handler is invoked when the render packages (specific to this node)  
        /// become available and in addition the node requests for associated render packages 
        /// if any for example, packages used for associated node manipulators
        /// </summary>
        private IEnumerable<IRenderPackage> OnRequestRenderPackages()
        {
            if (RequestRenderPackages != null)
            {
                return RequestRenderPackages();
            }
            return new List<IRenderPackage>();
        }

        /// <summary>
        /// Gets list of drawable Ids as registered with visualization manager 
        /// for all the output port of the given node.
        /// </summary>
        /// <returns>List of Drawable Ids</returns>
        private IEnumerable<string> GetDrawableIds()
        {
            var drawables = new List<String>();
            for (int i = 0; i < OutPortData.Count; ++i)
            {
                string id = GetDrawableId(i);
                if (!string.IsNullOrEmpty(id))
                    drawables.Add(id);
            }

            return drawables;
        }

        /// <summary>
        /// Gets the drawable Id as registered with visualization manager for
        /// the given output port on the given node.
        /// </summary>
        /// <param name="outPortIndex">Output port index</param>
        /// <returns>Drawable Id</returns>
        private string GetDrawableId(int outPortIndex)
        {
            var output = GetAstIdentifierForOutputIndex(outPortIndex);
            return output == null ? null : output.Value;
        }

        #endregion

        #region Node Migration Helper Methods

        protected static NodeMigrationData MigrateToDsFunction(
            NodeMigrationData data, string nickname, string funcName)
        {
            return MigrateToDsFunction(data, "", nickname, funcName);
        }

        protected static NodeMigrationData MigrateToDsFunction(
            NodeMigrationData data, string assembly, string nickname, string funcName)
        {
            XmlElement xmlNode = data.MigratedNodes.ElementAt(0);
            var element = MigrationManager.CreateFunctionNodeFrom(xmlNode);
            element.SetAttribute("assembly", assembly);
            element.SetAttribute("nickname", nickname);
            element.SetAttribute("function", funcName);

            var migrationData = new NodeMigrationData(data.Document);
            migrationData.AppendNode(element);
            return migrationData;
        }

        protected static NodeMigrationData MigrateToDsVarArgFunction(
            NodeMigrationData data, string assembly, string nickname, string funcName)
        {
            XmlElement xmlNode = data.MigratedNodes.ElementAt(0);
            var element = MigrationManager.CreateVarArgFunctionNodeFrom(xmlNode);
            element.SetAttribute("assembly", assembly);
            element.SetAttribute("nickname", nickname);
            element.SetAttribute("function", funcName);

            var migrationData = new NodeMigrationData(data.Document);
            migrationData.AppendNode(element);
            return migrationData;
        }

        #endregion

        public bool ShouldDisplayPreview
        {
            get
            {
                return ShouldDisplayPreviewCore;
            }
        }

        protected bool ShouldDisplayPreviewCore { get; set; }

        public event Action<NodeModel, IEnumerable<IRenderPackage>> RenderPackagesUpdated;

        private void OnRenderPackagesUpdated(IEnumerable<IRenderPackage> packages)
        {
            if (RenderPackagesUpdated != null)
            {
                RenderPackagesUpdated(this, packages);
            }
        }
    }

    public enum ElementState
    {
        Dead,
        Active,
        Warning,
        PersistentWarning,
        Error,
        AstBuildBroken
    };

    public enum LacingStrategy
    {
        Disabled,
        First,
        Shortest,
        Longest,
        CrossProduct
    };

    public enum PortEventType
    {
        MouseEnter,
        MouseLeave,
        MouseLeftButtonDown
    };

    public enum PortPosition
    {
        First,
        Top,
        Middle,
        Last
    }

    [Flags]
    public enum SnapExtensionEdges
    {
        None,
        Top = 0x1,
        Bottom = 0x2
    }

    public delegate void PortsChangedHandler(object sender, EventArgs e);

    internal delegate void DispatchedToUIThreadHandler(object sender, UIDispatcherEventArgs e);

    public class UIDispatcherEventArgs : EventArgs
    {
        public UIDispatcherEventArgs(Action a)
        {
            ActionToDispatch = a;
        }

        public Action ActionToDispatch { get; set; }
        public List<object> Parameters { get; set; }
    }
}
