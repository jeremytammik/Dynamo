﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Xml;
using Autodesk.DesignScript.Interfaces;
using Dynamo.Controls;
using Dynamo.Graph.Nodes;
using Dynamo.Graph.Nodes.CustomNodes;
using Dynamo.Graph.Workspaces;
using Dynamo.Logging;
using Dynamo.Selection;
using Dynamo.ViewModels;
using Dynamo.Wpf.Properties;
using Dynamo.Wpf.Rendering;
using DynamoUtilities;
using HelixToolkit.Wpf;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.Wpf.SharpDX.Core;
using SharpDX;
using Color = SharpDX.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using GeometryModel3D = HelixToolkit.Wpf.SharpDX.GeometryModel3D;
using MeshBuilder = HelixToolkit.Wpf.SharpDX.MeshBuilder;
using MeshGeometry3D = HelixToolkit.Wpf.SharpDX.MeshGeometry3D;
using Model3D = HelixToolkit.Wpf.SharpDX.Model3D;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using TextInfo = HelixToolkit.Wpf.SharpDX.TextInfo;

namespace Dynamo.Wpf.ViewModels.Watch3D
{
    public class CameraData
    { 
        // Default camera position data. These values have been rounded
        // to the nearest whole value.
        // eyeX="-16.9655136013663" eyeY="24.341577725171" eyeZ="50.6494323150915" 
        // lookX="12.4441040333119" lookY="-13.0110656299395" lookZ="-58.5449065206009" 
        // upX="-0.0812375695793365" upY="0.920504853452448" upZ="0.3821927158638" />

        private readonly Vector3D defaultCameraLookDirection = new Vector3D(12, -13, -58);
        private readonly Point3D defaultCameraPosition = new Point3D(-17, 24, 50);
        private readonly Vector3D defaultCameraUpDirection = new Vector3D(0, 1, 0);
        private const double defaultNearPlaneDistance = 0.1;
        private const double defaultFarPlaneDistance = 10000000;
         
        public Point3D EyePosition { get; set; }
        public Vector3D UpDirection { get; set; }
        public Vector3D LookDirection { get; set; }
        public string Name { get; set; }
        public double NearPlaneDistance { get; set; }
        public double FarPlaneDistance { get; set; }

        public CameraData()
        {
            Name = "Default Camera";
            EyePosition = defaultCameraPosition;
            UpDirection = defaultCameraUpDirection;
            LookDirection = defaultCameraLookDirection;
            NearPlaneDistance = defaultNearPlaneDistance;
            FarPlaneDistance = defaultFarPlaneDistance;
        }
    }

    /// <summary>
    /// The HelixWatch3DViewModel establishes a full rendering 
    /// context using the HelixToolkit. An instance of this class
    /// can act as the data source for a <see cref="Watch3DView"/>
    /// </summary>
    public class HelixWatch3DViewModel : DefaultWatch3DViewModel
    {
        #region private members

        private double lightAzimuthDegrees = 45.0;
        private double lightElevationDegrees = 35.0;
        private DynamoLineGeometryModel3D gridModel3D;
        private LineGeometry3D worldGrid;
        private LineGeometry3D worldAxes;
        private RenderTechnique renderTechnique;
        private PerspectiveCamera camera;
        private Vector3 directionalLightDirection = new Vector3(-0.5f, -1.0f, 0.0f);
        private DirectionalLight3D directionalLight;

        private readonly Color4 directionalLightColor = new Color4(0.9f, 0.9f, 0.9f, 1.0f);
        private readonly Color4 defaultSelectionColor = new Color4(new Color3(0, 158.0f / 255.0f, 1.0f));
        private readonly Color4 defaultMaterialColor = new Color4(new Color3(1.0f, 1.0f, 1.0f));
        private readonly Size defaultPointSize = new Size(6, 6);
        private readonly Size highlightSize = new Size(8, 8);
        private readonly Color4 highlightColor = new Color4(new Color3(1.0f, 0.0f, 0.0f));

        private static readonly Color4 defaultLineColor = new Color4(new Color3(0, 0, 0));
        private static readonly Color4 defaultPointColor = new Color4(new Color3(0, 0, 0));
        private static readonly Color4 defaultDeadColor = new Color4(new Color3(0.7f,0.7f,0.7f));
        private static readonly float defaultDeadAlphaScale = 0.2f;

        internal const string DefaultGridName = "Grid";
        internal const string DefaultAxesName = "Axes";
        internal const string DefaultLightName = "DirectionalLight";

        private const string PointsKey = ":points";
        private const string LinesKey = ":lines";
        private const string MeshKey = ":mesh";
        private const string TextKey = ":text";

        private const int FrameUpdateSkipCount = 200;
        private int currentFrameSkipCount;

        private const double EqualityTolerance = 0.000001;
        private double nearPlaneDistanceFactor = 0.001;
        internal const double DefaultNearClipDistance = 0.1f;
        internal const double DefaultFarClipDistance = 100000;
        internal static BoundingBox DefaultBounds = new BoundingBox(new Vector3(-25f, -25f, -25f), new Vector3(25f,25f,25f));

#if DEBUG
        private readonly Stopwatch renderTimer = new Stopwatch();
#endif

        #endregion

        #region events

        public Object Model3DDictionaryMutex = new object();
        private Dictionary<string, Model3D> model3DDictionary = new Dictionary<string, Model3D>();

        public event Action RequestViewRefresh;
        protected void OnRequestViewRefresh()
        {
            if (RequestViewRefresh != null)
            {
                RequestViewRefresh();
            }
        }

        protected override void OnActiveStateChanged()
        {
            preferences.IsBackgroundPreviewActive = active;

            if (!active && CanNavigateBackground)
            {
                CanNavigateBackground = false;
            }

            RaisePropertyChanged("IsGridVisible");
        }

        public event Action<Model3D> RequestAttachToScene;
        protected void OnRequestAttachToScene(Model3D model3D)
        {
            if (RequestAttachToScene != null)
            {
                RequestAttachToScene(model3D);
            }
        }

        public event Action<IEnumerable<IRenderPackage>, bool> RequestCreateModels;
        public void OnRequestCreateModels(IEnumerable<IRenderPackage> packages, bool forceAsyncCall = false)
        {
            if (RequestCreateModels != null)
            {
                RequestCreateModels(packages, forceAsyncCall);
            }
        }

        /// <summary>
        /// An event requesting a zoom to fit operation around the provided bounds.
        /// </summary>
        public event Action<BoundingBox> RequestZoomToFit;
        protected void OnRequestZoomToFit(BoundingBox bounds)
        {
            if(RequestZoomToFit != null)
            {
                RequestZoomToFit(bounds);
            }
        }

        #endregion

        #region properties

        internal static Color4 DefaultLineColor
        {
            get { return defaultLineColor; }
        }

        internal static Color4 DefaultPointColor
        {
            get { return defaultPointColor; }
        }

        internal static Color4 DefaultDeadColor
        {
            get { return defaultDeadColor; }
        }

        internal Dictionary<string, Model3D> Model3DDictionary
        {
            get
            {
                lock (Model3DDictionaryMutex)
                {
                    return model3DDictionary;
                }
            }

            set
            {
                lock (Model3DDictionaryMutex)
                {
                    model3DDictionary = value;
                }
            }
        }

        public LineGeometry3D Grid
        {
            get { return worldGrid; }
            set
            {
                worldGrid = value;
                RaisePropertyChanged(DefaultGridName);
            }
        }

        public LineGeometry3D Axes
        {
            get { return worldAxes; }
            set
            {
                worldAxes = value;
                RaisePropertyChanged("Axes");
            }
        }

        public PhongMaterial WhiteMaterial { get; set; }

        public PhongMaterial SelectedMaterial { get; set; }

        public Transform3D Model1Transform { get; private set; }

        public RenderTechnique RenderTechnique
        {
            get
            {
                return this.renderTechnique;
            }
            set
            {
                renderTechnique = value;
                RaisePropertyChanged("RenderTechnique");
            }
        }

        public PerspectiveCamera Camera
        {
            get
            {
                return this.camera;
            }

            set
            {
                camera = value;
                RaisePropertyChanged("Camera");
            }
        }

        public double LightAzimuthDegrees
        {
            get { return lightAzimuthDegrees; }
            set { lightAzimuthDegrees = value; }
        }

        public double LightElevationDegrees
        {
            get { return lightElevationDegrees; }
            set { lightElevationDegrees = value; }
        }

        public double NearPlaneDistanceFactor
        {
            get { return nearPlaneDistanceFactor; }
            set
            {
                nearPlaneDistanceFactor = value;
                RaisePropertyChanged("NearPlaneDistanceFactor");
            }
        }

        public override bool IsGridVisible
        {
            get { return isGridVisible && Active; }
            set
            {
                if (isGridVisible == value) return;

                base.IsGridVisible = value;
                SetGridVisibility();
            }
        }

        /// <summary>
        /// The LeftClickCommand is set according to the
        /// ViewModel's IsPanning or IsOrbiting properties.
        /// When those properties are changed, this command is
        /// set to ViewportCommand.Pan or ViewportCommand.Rotate depending. 
        /// If neither panning or rotating is set, this property is set to null 
        /// and left clicking should have no effect.
        /// </summary>
        public RoutedCommand LeftClickCommand
        {
            get
            {
                if (IsPanning) return ViewportCommands.Pan;
                if (IsOrbiting) return ViewportCommands.Rotate;

                return null;
            }
        }

        public bool IsResizable { get; protected set; }

        public IEnumerable<Model3D> SceneItems
        {
            get
            {
                if (Model3DDictionary == null)
                {
                    return new List<Model3D>();
                }

                var values = Model3DDictionary.Values.ToList();
                //values.Sort(new Model3DComparer(Camera.Position));
                return values;
            }
        }

        public IEffectsManager EffectsManager { get; private set; }

        public IRenderTechniquesManager RenderTechniquesManager { get; private set; }

        public bool SupportDeferredRender { get; private set; }

        #endregion

        /// <summary>
        /// Attempt to create a HelixWatch3DViewModel. If one cannot be created,
        /// fall back to creating a DefaultWatch3DViewModel and log the exception.
        /// </summary>
        /// <param name="parameters">A Watch3DViewModelStartupParams object.</param>
        /// <param name="logger">A logger to be used to log the exception.</param>
        /// <returns></returns>
        public static DefaultWatch3DViewModel TryCreateHelixWatch3DViewModel(Watch3DViewModelStartupParams parameters, DynamoLogger logger)
        {
            try
            {
                var vm = new HelixWatch3DViewModel(parameters);
                return vm;
            }
            catch (Exception ex)
            {
                logger.Log(Resources.BackgroundPreviewCreationFailureMessage, LogLevel.Console);
                logger.Log(ex.Message, LogLevel.File);

                var vm = new DefaultWatch3DViewModel(parameters)
                {
                    Active = false,
                    CanBeActivated = false
                };
                return vm;
            }
        }

        protected HelixWatch3DViewModel(Watch3DViewModelStartupParams parameters) : base(parameters)
        {
            Name = Resources.BackgroundPreviewName;
            IsResizable = false;
            RenderTechniquesManager = new DynamoRenderTechniquesManager();
            EffectsManager = new DynamoEffectsManager(RenderTechniquesManager);

            SetupScene();
            InitializeHelix();
        }

        public void SerializeCamera(XmlElement camerasElement)
        {
            if (camera == null) return;

            try
            {
                var node = XmlHelper.AddNode(camerasElement, "Camera");
                XmlHelper.AddAttribute(node, "Name", Name);
                XmlHelper.AddAttribute(node, "eyeX", camera.Position.X.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "eyeY", camera.Position.Y.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "eyeZ", camera.Position.Z.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "lookX", camera.LookDirection.X.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "lookY", camera.LookDirection.Y.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "lookZ", camera.LookDirection.Z.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "upX", camera.UpDirection.X.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "upY", camera.UpDirection.Y.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "upZ", camera.UpDirection.Z.ToString(CultureInfo.InvariantCulture));
                camerasElement.AppendChild(node);

            }
            catch (Exception ex)
            {
                logger.LogError(Properties.Resources.CameraDataSaveError);
                logger.Log(ex);
            }
        }

        /// <summary>
        /// Create a CameraData object from an XmlNode representing the Camera's serialized
        /// position data.
        /// </summary>
        /// <param name="cameraNode">The XmlNode containing the camera position data.</param>
        /// <returns></returns>
        public CameraData DeserializeCamera(XmlNode cameraNode)
        {
            if (cameraNode.Attributes == null || cameraNode.Attributes.Count == 0)
            {
                return new CameraData();
            }

            try
            {
                var name = cameraNode.Attributes["Name"].Value;
                var ex = float.Parse(cameraNode.Attributes["eyeX"].Value, CultureInfo.InvariantCulture);
                var ey = float.Parse(cameraNode.Attributes["eyeY"].Value, CultureInfo.InvariantCulture);
                var ez = float.Parse(cameraNode.Attributes["eyeZ"].Value, CultureInfo.InvariantCulture);
                var lx = float.Parse(cameraNode.Attributes["lookX"].Value, CultureInfo.InvariantCulture);
                var ly = float.Parse(cameraNode.Attributes["lookY"].Value, CultureInfo.InvariantCulture);
                var lz = float.Parse(cameraNode.Attributes["lookZ"].Value, CultureInfo.InvariantCulture);
                var ux = float.Parse(cameraNode.Attributes["upX"].Value, CultureInfo.InvariantCulture);
                var uy = float.Parse(cameraNode.Attributes["upY"].Value, CultureInfo.InvariantCulture);
                var uz = float.Parse(cameraNode.Attributes["upZ"].Value, CultureInfo.InvariantCulture);

                var camData = new CameraData
                {
                    Name = name,
                    EyePosition = new Point3D(ex, ey, ez),
                    LookDirection = new Vector3D(lx, ly, lz),
                    UpDirection = new Vector3D(ux, uy, uz)
                };

                return camData;
            }
            catch (Exception ex)
            {
                logger.LogError(Properties.Resources.CameraDataLoadError);
                logger.Log(ex);
            }

            return new CameraData();
        }

        public override void AddGeometryForRenderPackages(IEnumerable<IRenderPackage> packages, bool forceAsyncCall = false)
        {
            if (Active == false)
            {
                return;
            }

            // Raise request for model objects to be
            // created on the UI thread.
            OnRequestCreateModels(packages, forceAsyncCall);
        }

        protected override void OnShutdown()
        {
            EffectsManager = null;
            RenderTechniquesManager = null;
        }

        protected override void OnClear()
        {
            lock (Model3DDictionaryMutex)
            {
                var keysList = new List<string> { DefaultLightName, DefaultGridName, DefaultAxesName };

                foreach (var key in Model3DDictionary.Keys.Except(keysList).ToList())
                {
                    var model = Model3DDictionary[key] as GeometryModel3D;
                    model.Detach();
                    Model3DDictionary.Remove(key);
                }
            }

            OnSceneItemsChanged();
        }

        protected override void OnWorkspaceCleared(WorkspaceModel workspace)
        {
            SetCameraData(new CameraData());
            base.OnWorkspaceCleared(workspace);
        }

        protected override void OnWorkspaceOpening(XmlDocument doc)
        {
            var camerasElements = doc.GetElementsByTagName("Cameras");
            if (camerasElements.Count == 0)
            {
                return;
            }

            foreach (XmlNode cameraNode in camerasElements[0].ChildNodes)
            {
                try
                {
                    var camData = DeserializeCamera(cameraNode);
                    SetCameraData(camData);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                    logger.Log(ex);
                }
            }
        }

        protected override void OnWorkspaceSaving(XmlDocument doc)
        {
            var root = doc.DocumentElement;
            if (root == null)
            {
                return;
            }

            var camerasElement = doc.CreateElement("Cameras");
            SerializeCamera(camerasElement);
            root.AppendChild(camerasElement);
        }

        protected override void SelectionChangedHandler(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Reset:
                    Model3DDictionary.Values.
                        Where(v => v is GeometryModel3D).
                        Cast<GeometryModel3D>().ToList().ForEach(g => g.SetValue(AttachedProperties.ShowSelectedProperty, false));
                    return;

                case NotifyCollectionChangedAction.Remove:
                    SetSelection(e.OldItems, false);
                    return;

                case NotifyCollectionChangedAction.Add:

                    // When a node is added to the workspace, it is also added
                    // to the selection. When running automatically, this addition
                    // also triggers an execution. This would successive calls to render.
                    // To prevent this, we maintain a collection of recently added nodes, and
                    // we check if the selection is an addition and if all of the recently
                    // added nodes are contained in that selection. if so, we skip the render
                    // as this render will occur after the upcoming execution.
                    if (e.Action == NotifyCollectionChangedAction.Add && recentlyAddedNodes.Any()
                        && recentlyAddedNodes.TrueForAll(n => e.NewItems.Contains((object)n)))
                    {
                        recentlyAddedNodes.Clear();
                        return;
                    }

                    SetSelection(e.NewItems, true);
                    return;
            }
        }

        protected override void OnNodePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var node = sender as NodeModel;
            if (node == null)
            {
                return;
            }
            node.WasRenderPackageUpdatedAfterExecution = false;

            switch (e.PropertyName)
            {
                case "CachedValue":
                    Debug.WriteLine(string.Format("Requesting render packages for {0}", node.GUID));
                    node.RequestVisualUpdateAsync(scheduler, engineManager.EngineController, renderPackageFactory);
                    break;

                case "DisplayLabels":
                    node.RequestVisualUpdateAsync(scheduler, engineManager.EngineController, renderPackageFactory, true);
                    break;

                case "IsVisible":
                    var geoms = FindAllGeometryModel3DsForNode(node.AstIdentifierBase);
                    foreach(var g in geoms)
                    {
                        g.Value.Visibility = node.IsVisible ? Visibility.Visible : Visibility.Hidden;
                        //RaisePropertyChanged("SceneItems");
                    }

                    node.RequestVisualUpdateAsync(scheduler, engineManager.EngineController, renderPackageFactory, true);
                    break;

                case "IsFrozen":
                    HashSet<NodeModel> gathered = new HashSet<NodeModel>();
                    node.GetDownstreamNodes(node, gathered);
                    SetGeometryFrozen(gathered);
                    break;
            }
        }

        public override void GenerateViewGeometryFromRenderPackagesAndRequestUpdate(IEnumerable<IRenderPackage> taskPackages)
        {
            foreach (var p in taskPackages)
            {
                Debug.WriteLine(string.Format("Processing render packages for {0}", p.Description));
            }

            recentlyAddedNodes.Clear();

#if DEBUG
            renderTimer.Start();
#endif
            var packages = taskPackages
                .Cast<HelixRenderPackage>().Where(rp => rp.MeshVertexCount % 3 == 0);

            RemoveGeometryForUpdatedPackages(packages);

            AggregateRenderPackages(packages);

#if DEBUG
            renderTimer.Stop();
            Debug.WriteLine(string.Format("RENDER: {0} ellapsed for compiling assets for rendering.", renderTimer.Elapsed));
            renderTimer.Reset();
            renderTimer.Start();
#endif

            OnSceneItemsChanged();
        }

        public override void DeleteGeometryForIdentifier(string identifier, bool requestUpdate = true)
        {
            lock (Model3DDictionaryMutex)
            {
                var geometryModels = FindAllGeometryModel3DsForNode(identifier);

                if (!geometryModels.Any())
                {
                    return;
                }

                foreach (var kvp in geometryModels)
                {
                    var model3D = Model3DDictionary[kvp.Key] as GeometryModel3D;
                    // check if the geometry is frozen. if the gemoetry is frozen 
                    // then do not detach from UI.
                    var frozenModel = AttachedProperties.GetIsFrozen(model3D);
                    if (!frozenModel)
                    {
                        if (model3D != null)
                        {
                            model3D.Detach();
                        }
                        Model3DDictionary.Remove(kvp.Key);
                    }
                }
            }

            if (!requestUpdate) return;

            OnSceneItemsChanged();
        }

        protected override void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "CurrentWorkspace":
                    OnClear();

                    IEnumerable<NodeModel> nodesToRender = null;

                    // Get the nodes to render from the current home workspace. For custom
                    // nodes, this will get the workspace in which the custom node is placed.
                    // This will need to be adapted when multiple home workspaces are supported,
                    // so that a specific workspace can be selected to act as the preview context.

                    var hs = model.Workspaces.FirstOrDefault(i => i is HomeWorkspaceModel);
                    if (hs != null)
                    {
                        nodesToRender = hs.Nodes;
                    }

                    if (nodesToRender == null)
                    {
                        return;
                    }

                    foreach (var node in nodesToRender)
                    {
                        node.RequestVisualUpdateAsync(scheduler, engineManager.EngineController, renderPackageFactory, true);
                    }

                    break;
            }
        }

        protected override void ZoomToFit(object parameter)
        {
            var idents = FindIdentifiersForContext();
            var geoms = SceneItems.Where(item => item is GeometryModel3D).Cast<GeometryModel3D>();
            var targetGeoms = FindGeometryForIdentifiers(geoms, idents);
            var selectionBounds = ComputeBoundsForGeometry(targetGeoms.ToArray());

            // Don't zoom if there is no valid bounds.
            if (selectionBounds.Equals(new BoundingBox())) return;

            OnRequestZoomToFit(selectionBounds);
        }

        public override CameraData GetCameraInformation()
        {
            return camera.ToCameraData(Name);
        }

        /// <summary>
        /// Finds all output identifiers based on the context.
        /// 
        /// Ex. If there are nodes selected, returns all identifiers for outputs
        /// on the selected nodes. If you're in a custom node, returns all identifiers
        /// for the outputs from instances of those custom nodes in the graph. etc.
        /// </summary>
        /// <returns>An <see cref="IEnumerable"/> of <see cref="string"/> containing the output identifiers found in the context.</returns>
        private IEnumerable<string> FindIdentifiersForContext()
        {
            IEnumerable<string> idents = null;

            var hs = model.Workspaces.OfType<HomeWorkspaceModel>().FirstOrDefault();
            if (hs == null)
            {
                return idents;
            }

            if (InCustomNode())
            {
                idents = FindIdentifiersForCustomNodes(hs);
            }
            else
            {
                if (DynamoSelection.Instance.Selection.Any())
                {
                    var selNodes = DynamoSelection.Instance.Selection.Where(s => s is NodeModel).Cast<NodeModel>().ToArray();
                    idents = FindIdentifiersForSelectedNodes(selNodes);
                }
                else
                {
                    idents = AllOutputIdentifiersInWorkspace(hs);
                }
            }

            return idents;
        } 

        protected override bool CanToggleCanNavigateBackground(object parameter)
        {
            return true;
        }

        #region internal methods

        internal void ComputeFrameUpdate()
        {
#if DEBUG
            if (renderTimer.IsRunning)
            {
                renderTimer.Stop();
                Debug.WriteLine(string.Format("RENDER: {0} ellapsed for setting properties and rendering.", renderTimer.Elapsed));
                renderTimer.Reset();
            }
#endif

            // Raising a property change notification for
            // the SceneItems collections causes a full
            // re-render including sorting for transparency.
            // We don't want to do this every frame, so we
            // do this update only at a fixed interval.
            //if (currentFrameSkipCount == FrameUpdateSkipCount)
            //{
            //    RaisePropertyChanged("SceneItems");
            //    currentFrameSkipCount = 0;
            //}

            currentFrameSkipCount++;
        }

        #endregion

        #region private methods

        private void OnSceneItemsChanged()
        {
            RaisePropertyChanged("SceneItems");
            OnRequestViewRefresh();
        }
   
        private KeyValuePair<string, Model3D>[] FindAllGeometryModel3DsForNode(string identifier)
        {
            KeyValuePair<string, Model3D>[] geometryModels;

            lock (Model3DDictionaryMutex)
            {
                geometryModels =
                    Model3DDictionary
                        .Where(x => x.Key.Contains(identifier))
                        .Where(x => x.Value is GeometryModel3D)
                        .Select(x => x).ToArray();
            }

            return geometryModels;
        }

        private void SetGeometryFrozen(HashSet<NodeModel> gathered)
        {
            foreach (var node in gathered)
            {
                var geometryModels = FindAllGeometryModel3DsForNode(node.AstIdentifierBase);

                if (!geometryModels.Any())
                {
                    continue;
                }

                var modelValues = geometryModels.Select(x => x.Value);

                foreach (GeometryModel3D g in modelValues)
                {
                    g.SetValue(AttachedProperties.IsFrozenProperty, node.IsFrozen);
                }
            }
        }

        private void SetSelection(IEnumerable items, bool isSelected)
        {
            foreach (var item in items)
            {
                var node = item as NodeModel;
                if (node == null)
                {
                    continue;
                }

                var geometryModels = FindAllGeometryModel3DsForNode(node.AstIdentifierBase);

                if (!geometryModels.Any())
                {
                    continue;
                }

                var modelValues = geometryModels.Select(x => x.Value);

                foreach(GeometryModel3D g in modelValues)
                {
                    g.SetValue(AttachedProperties.ShowSelectedProperty, isSelected);
                }
            }
        }

        private void LogCameraWarning(string msg, Exception ex)
        {
            logger.LogWarning(msg, WarningLevel.Mild);
            logger.Log(msg);
            logger.Log(ex.Message);
        }

        private void SaveCamera(XmlElement camerasElement)
        {
            try
            {
                var node = XmlHelper.AddNode(camerasElement, "Camera");
                XmlHelper.AddAttribute(node, "Name", Name);
                XmlHelper.AddAttribute(node, "eyeX", Camera.Position.X.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "eyeY", Camera.Position.Y.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "eyeZ", Camera.Position.Z.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "lookX", Camera.LookDirection.X.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "lookY", Camera.LookDirection.Y.ToString(CultureInfo.InvariantCulture));
                XmlHelper.AddAttribute(node, "lookZ", Camera.LookDirection.Z.ToString(CultureInfo.InvariantCulture));
                camerasElement.AppendChild(node);
            }
            catch (Exception ex)
            {
                const string msg = "CAMERA: Camera position information could not be saved.";
                LogCameraWarning(msg, ex);
            }
        }

        private void LoadCamera(XmlNode cameraNode)
        {
            if (cameraNode.Attributes.Count == 0)
            {
                return;
            }

            try
            {
                Name = cameraNode.Attributes["Name"].Value;
                var ex = float.Parse(cameraNode.Attributes["eyeX"].Value);
                var ey = float.Parse(cameraNode.Attributes["eyeY"].Value);
                var ez = float.Parse(cameraNode.Attributes["eyeZ"].Value);
                var lx = float.Parse(cameraNode.Attributes["lookX"].Value);
                var ly = float.Parse(cameraNode.Attributes["lookY"].Value);
                var lz = float.Parse(cameraNode.Attributes["lookZ"].Value);

                Camera.LookDirection = new Vector3D(lx, ly, lz);
                Camera.Position = new Point3D(ex, ey, ez);
            }
            catch (Exception ex)
            {
                const string msg = "CAMERA: Camera position information could not be loaded from the file.";
                LogCameraWarning(msg, ex);
            }
        }

        private void SetupScene()
        {
            RenderTechnique = new RenderTechnique("RenderCustom");

            WhiteMaterial = new PhongMaterial
            {
                Name = "White",
                AmbientColor = PhongMaterials.ToColor(0.1, 0.1, 0.1, 1.0),
                DiffuseColor = defaultMaterialColor,
                SpecularColor = PhongMaterials.ToColor(0.0225, 0.0225, 0.0225, 1.0),
                EmissiveColor = PhongMaterials.ToColor(0.0, 0.0, 0.0, 1.0),
                SpecularShininess = 12.8f,
            };

            SelectedMaterial = new PhongMaterial
            {
                Name = "White",
                AmbientColor = PhongMaterials.ToColor(0.1, 0.1, 0.1, 1.0),
                DiffuseColor = defaultSelectionColor,
                SpecularColor = PhongMaterials.ToColor(0.0225, 0.0225, 0.0225, 1.0),
                EmissiveColor = PhongMaterials.ToColor(0.0, 0.0, 0.0, 1.0),
                SpecularShininess = 12.8f,
            };

            Model1Transform = new TranslateTransform3D(0, -0, 0);

            // camera setup
            Camera = new PerspectiveCamera();

            SetCameraData(new CameraData());

            DrawGrid();
        }

        /// <summary>
        /// Initialize the Helix with these values. These values should be attached before the 
        /// visualization starts. Deleting them and attaching them does not make any effect on helix.         
        /// So they are initialized before the process starts.
        /// </summary>
        private void InitializeHelix()
        {
            if (Model3DDictionary == null)
            {
                throw new Exception("Helix could not be initialized.");
            }

            directionalLight = new DirectionalLight3D
            {
                Color = directionalLightColor,
                Direction = directionalLightDirection,
                Name = DefaultLightName
            };

            if (!Model3DDictionary.ContainsKey(DefaultLightName))
            {
                Model3DDictionary.Add(DefaultLightName, directionalLight);
            }

            gridModel3D = new DynamoLineGeometryModel3D
            {
                Geometry = Grid,
                Transform = Model1Transform,
                Color = Color.White,
                Thickness = 0.3,
                IsHitTestVisible = false,
                Name = DefaultGridName
            };

            SetGridVisibility();

            if (!model3DDictionary.ContainsKey(DefaultGridName))
            {
                Model3DDictionary.Add(DefaultGridName, gridModel3D);
            }

            var axesModel3D = new DynamoLineGeometryModel3D
            {
                Geometry = Axes,
                Transform = Model1Transform,
                Color = Color.White,
                Thickness = 0.3,
                IsHitTestVisible = false,
                Name = DefaultAxesName
            };

            if (!Model3DDictionary.ContainsKey(DefaultAxesName))
            {
                Model3DDictionary.Add(DefaultAxesName, axesModel3D);
            }
        }

        /// <summary>
        /// Create the grid
        /// </summary>
        private void DrawGrid()
        {
            Grid = new LineGeometry3D();
            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var colors = new Color4Collection();

            for (var i = 0; i < 10; i += 1)
            {
                for (var j = 0; j < 10; j += 1)
                {
                    DrawGridPatch(positions, indices, colors, -50 + i * 10, -50 + j * 10);
                }
            }

            Grid.Positions = positions;
            Grid.Indices = indices;
            Grid.Colors = colors;

            Axes = new LineGeometry3D();
            var axesPositions = new Vector3Collection();
            var axesIndices = new IntCollection();
            var axesColors = new Color4Collection();

            // Draw the coordinate axes
            axesPositions.Add(new Vector3());
            axesIndices.Add(axesPositions.Count - 1);
            axesPositions.Add(new Vector3(50, 0, 0));
            axesIndices.Add(axesPositions.Count - 1);
            axesColors.Add(Color.Red);
            axesColors.Add(Color.Red);

            axesPositions.Add(new Vector3());
            axesIndices.Add(axesPositions.Count - 1);
            axesPositions.Add(new Vector3(0, 5, 0));
            axesIndices.Add(axesPositions.Count - 1);
            axesColors.Add(Color.Blue);
            axesColors.Add(Color.Blue);

            axesPositions.Add(new Vector3());
            axesIndices.Add(axesPositions.Count - 1);
            axesPositions.Add(new Vector3(0, 0, -50));
            axesIndices.Add(axesPositions.Count - 1);
            axesColors.Add(Color.Green);
            axesColors.Add(Color.Green);

            Axes.Positions = axesPositions;
            Axes.Indices = axesIndices;
            Axes.Colors = axesColors;
        }

        private void SetGridVisibility()
        {
            var visibility = isGridVisible ? Visibility.Visible : Visibility.Hidden;
            //return if there is nothing to change
            if (gridModel3D.Visibility == visibility) return;
            
            gridModel3D.Visibility = visibility;
            OnRequestViewRefresh();
        }

        private static void DrawGridPatch(
            Vector3Collection positions, IntCollection indices, Color4Collection colors, int startX, int startY)
        {
            var c1 = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#c5d1d8");
            c1.Clamp();
            var c2 = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#ddeaf2");
            c2.Clamp();

            var darkGridColor = new Color4(new Vector4(c1.ScR, c1.ScG, c1.ScB, 1));
            var lightGridColor = new Color4(new Vector4(c2.ScR, c2.ScG, c2.ScB, 1));

            const int size = 10;

            for (var x = startX; x <= startX + size; x++)
            {
                if (x == 0 && startY < 0) continue;

                var v = new Vector3(x, -.001f, startY);
                positions.Add(v);
                indices.Add(positions.Count - 1);
                positions.Add(new Vector3(x, -.001f, startY + size));
                indices.Add(positions.Count - 1);

                if (x % 5 == 0)
                {
                    colors.Add(darkGridColor);
                    colors.Add(darkGridColor);
                }
                else
                {
                    colors.Add(lightGridColor);
                    colors.Add(lightGridColor);
                }
            }

            for (var y = startY; y <= startY + size; y++)
            {
                if (y == 0 && startX >= 0) continue;

                positions.Add(new Vector3(startX, -.001f, y));
                indices.Add(positions.Count - 1);
                positions.Add(new Vector3(startX + size, -.001f, y));
                indices.Add(positions.Count - 1);

                if (y % 5 == 0)
                {
                    colors.Add(darkGridColor);
                    colors.Add(darkGridColor);
                }
                else
                {
                    colors.Add(lightGridColor);
                    colors.Add(lightGridColor);
                }
            }
        }

        public void SetCameraData(CameraData data)
        {
            if (Camera == null) return;

            Camera.LookDirection = data.LookDirection;
            Camera.Position = data.EyePosition;
            Camera.UpDirection = data.UpDirection;
            Camera.NearPlaneDistance = data.NearPlaneDistance;
            Camera.FarPlaneDistance = data.FarPlaneDistance;
        }

        private void RemoveGeometryForUpdatedPackages(IEnumerable<IRenderPackage> packages)
        {
            lock (Model3DDictionaryMutex)
            {
                var packageDescrips = packages.Select(p => p.Description.Split(':')[0]).Distinct();

                foreach (var id in packageDescrips)
                {
                    DeleteGeometryForIdentifier(id, false);
                }
            }
        }

        private bool InCustomNode()
        {
            return model.CurrentWorkspace is CustomNodeWorkspaceModel;
        }

        /// <summary>
        /// Given a collection of render packages, generates
        /// corresponding <see cref="GeometryModel3D"/> objects for visualization, and
        /// attaches them to the visual scene.
        /// </summary>
        /// <param name="packages">An <see cref="IEnumerable"/> of <see cref="HelixRenderPackage"/>.</param>
        private void AggregateRenderPackages(IEnumerable<HelixRenderPackage> packages)
        {
            
            IEnumerable<string> customNodeIdents = null;
            if (InCustomNode())
            {
                var hs = model.Workspaces.OfType<HomeWorkspaceModel>().FirstOrDefault();
                if (hs != null)
                {
                    customNodeIdents = FindIdentifiersForCustomNodes((HomeWorkspaceModel)hs);
                }
            }

            lock (Model3DDictionaryMutex)
            {
                foreach (var rp in packages)
                {
                    // Each node can produce multiple render packages. We want all the geometry of the
                    // same kind stored inside a RenderPackage to be pushed into one GeometryModel3D object.
                    // We strip the unique identifier for the package (i.e. the bit after the `:` in var12345:0), and replace it
                    // with `points`, `lines`, or `mesh`. For each RenderPackage, we check whether the geometry dictionary
                    // has entries for the points, lines, or mesh already. If so, we add the RenderPackage's geometry
                    // to those geometry objects.
                    
                    var baseId = rp.Description;
                    if (baseId.IndexOf(":", StringComparison.Ordinal) > 0)
                    {
                        baseId = baseId.Split(':')[0];
                    }
                    var id = baseId;
                    //If this render package belongs to special render package, then create
                    //and update the corresponding GeometryModel. Sepcial renderpackage are
                    //defined based on its description containing one of the constants from
                    //RenderDescriptions struct.
                    if (UpdateGeometryModelForSpecialRenderPackage(rp, id))
                        continue;

                    var drawDead = InCustomNode() && !customNodeIdents.Contains(baseId);

                    var p = rp.Points;
                    if (p.Positions.Any())
                    {
                        id = baseId + PointsKey;

                        PointGeometryModel3D pointGeometry3D;

                        if (Model3DDictionary.ContainsKey(id))
                        {
                            pointGeometry3D = Model3DDictionary[id] as PointGeometryModel3D;
                        }
                        else
                        {
                            pointGeometry3D = CreatePointGeometryModel3D(rp);
                            Model3DDictionary.Add(id, pointGeometry3D);
                        }

                        var points = pointGeometry3D.Geometry as PointGeometry3D;
                        var startIdx = points.Positions.Count;

                        points.Positions.AddRange(p.Positions);

                        if (drawDead)
                        {
                            points.Colors.AddRange(Enumerable.Repeat(defaultDeadColor, points.Positions.Count));
                        }
                        else
                        {
                            points.Colors.AddRange(p.Colors.Any()
                              ? p.Colors
                              : Enumerable.Repeat(defaultPointColor, points.Positions.Count));
                            points.Indices.AddRange(p.Indices.Select(i => i + startIdx));
                        }
                        

                        if (rp.DisplayLabels)
                        {
                            CreateOrUpdateText(baseId, p.Positions[0], rp);
                        }

                        pointGeometry3D.Geometry = points;
                        pointGeometry3D.Name = baseId;
                    }

                    var l = rp.Lines;
                    if (l.Positions.Any())
                    {
                        id = baseId + LinesKey;

                        LineGeometryModel3D lineGeometry3D;

                        if (Model3DDictionary.ContainsKey(id))
                        {
                            lineGeometry3D = Model3DDictionary[id] as LineGeometryModel3D;
                        }
                        else
                        {
                            // If the package contains mesh vertices, then the lines represent the 
                            // edges of meshes. Draw them with a different thickness.
                            lineGeometry3D = CreateLineGeometryModel3D(rp, rp.MeshVertices.Any()?0.5:1.0);
                            Model3DDictionary.Add(id, lineGeometry3D);
                        }

                        var lineSet = lineGeometry3D.Geometry as LineGeometry3D;
                        var startIdx = lineSet.Positions.Count;

                        lineSet.Positions.AddRange(l.Positions);
                        if (drawDead)
                        {
                            lineSet.Colors.AddRange(Enumerable.Repeat(defaultDeadColor, l.Positions.Count));
                        }
                        else
                        {
                            lineSet.Colors.AddRange(l.Colors.Any()
                             ? l.Colors
                             : Enumerable.Repeat(defaultLineColor, l.Positions.Count));
                        }
                        
                        lineSet.Indices.AddRange(l.Indices.Any()
                            ? l.Indices.Select(i => i + startIdx)
                            : Enumerable.Range(startIdx, startIdx + l.Positions.Count));

                        if (rp.DisplayLabels)
                        {
                            var pt = lineSet.Positions[startIdx];
                            CreateOrUpdateText(baseId, pt, rp);
                        }

                        lineGeometry3D.Geometry = lineSet;
                        lineGeometry3D.Name = baseId;
                    }

                    var m = rp.Mesh;
                    if (!m.Positions.Any()) continue;

                    id = ((rp.RequiresPerVertexColoration || rp.Colors != null) ? rp.Description : baseId) + MeshKey;

                    DynamoGeometryModel3D meshGeometry3D;

                    if (Model3DDictionary.ContainsKey(id))
                    {
                        meshGeometry3D = Model3DDictionary[id] as DynamoGeometryModel3D;
                    }
                    else
                    {
                        meshGeometry3D = CreateDynamoGeometryModel3D(rp);
                        Model3DDictionary.Add(id, meshGeometry3D);
                    }

                    var mesh = meshGeometry3D.Geometry == null
                        ? HelixRenderPackage.InitMeshGeometry()
                        : meshGeometry3D.Geometry as MeshGeometry3D;
                    var idxCount = mesh.Positions.Count;

                    mesh.Positions.AddRange(m.Positions);

                    // If we are in a custom node, and the current
                    // package's id is NOT one of the output ids of custom nodes
                    // in the graph, then draw the geometry with transparency.
                    if (drawDead)
                    {
                        meshGeometry3D.RequiresPerVertexColoration = true;
                        mesh.Colors.AddRange(m.Colors.Select(c=>new Color4(c.Red, c.Green, c.Blue, c.Alpha * defaultDeadAlphaScale)));
                    }
                    else
                    {
                        mesh.Colors.AddRange(m.Colors);
                    }

                    mesh.Normals.AddRange(m.Normals);
                    mesh.TextureCoordinates.AddRange(m.TextureCoordinates);
                    mesh.Indices.AddRange(m.Indices.Select(i => i + idxCount));

                    if (mesh.Colors.Any(c => c.Alpha < 1.0))
                    {
                        meshGeometry3D.SetValue(AttachedProperties.HasTransparencyProperty, true);
                    }

                    if (rp.DisplayLabels)
                    {
                        var pt = mesh.Positions[idxCount];
                        CreateOrUpdateText(baseId, pt, rp);
                    }

                    meshGeometry3D.Geometry = mesh;
                    meshGeometry3D.Name = baseId;
                }

                AttachAllGeometryModel3DToRenderHost();
            }
        }

        /// <summary>
        /// Updates or replaces the GeometryModel3D for special IRenderPackage. Special 
        /// IRenderPackage has a Description field that starts with a string value defined 
        /// in RenderDescriptions. See RenderDescriptions for details of possible values.
        /// </summary>
        /// <param name="rp">The target HelixRenderPackage object</param>
        /// <param name="id">id of the HelixRenderPackage object</param>
        /// <returns>Returns true if rp is a special render package, and its GeometryModel3D
        /// is successfully updated.</returns>
        private bool UpdateGeometryModelForSpecialRenderPackage(HelixRenderPackage rp, string id)
        {
            int desclength = RenderDescriptions.ManipulatorAxis.Length;
            if (rp.Description.Length < desclength)
                return false;

            string description = rp.Description.Substring(0, desclength);
            Model3D model = null;
            Model3DDictionary.TryGetValue(id, out model);
            switch(description)
            {
                case RenderDescriptions.ManipulatorAxis:
                    var manipulator = model as DynamoGeometryModel3D;
                    if (null == manipulator)
                        manipulator = CreateDynamoGeometryModel3D(rp);
                    
                    var mb = new MeshBuilder();
                    mb.AddArrow(rp.Lines.Positions[0], rp.Lines.Positions[1], 0.1);
                    manipulator.Geometry = mb.ToMeshGeometry3D();
                    
                    if (rp.Lines.Colors[0].Red == 1)
                        manipulator.Material = PhongMaterials.Red;
                    else if (rp.Lines.Colors[0].Green == 1)
                        manipulator.Material = PhongMaterials.Green;
                    else if (rp.Lines.Colors[0].Blue == 1)
                        manipulator.Material = PhongMaterials.Blue;

                    Model3DDictionary[id] = manipulator;
                    return true;
                case RenderDescriptions.AxisLine:
                    var centerline = model as DynamoLineGeometryModel3D;
                    if (null == centerline)
                        centerline = CreateLineGeometryModel3D(rp, 0.3);
                    centerline.Geometry = rp.Lines;
                    Model3DDictionary[id] = centerline;
                    return true;
                case RenderDescriptions.ManipulatorPlane:
                    var plane = model as DynamoLineGeometryModel3D;
                    if (null == plane)
                        plane = CreateLineGeometryModel3D(rp, 0.7);
                    plane.Geometry = rp.Lines;
                    Model3DDictionary[id] = plane;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handles View Click event and performs a hit tests for geometry selection.
        /// </summary>
        protected override void HandleViewClick(object sender, MouseButtonEventArgs e)
        {
            var viewport = sender as Viewport3DX;
            if (null == viewport) return;

            var vm = viewModel as DynamoViewModel;
            if (vm == null) return;

            var nodes = new List<NodeModel>();
            var hits = viewport.FindHits(e.GetPosition(viewport));
            foreach (var hit in hits)
            {
                var model = hit.ModelHit;
                if (model == null) continue;

                foreach (var node in vm.Model.CurrentWorkspace.Nodes)
                {
                    var foundNode = node.AstIdentifierBase.Contains(model.Name);

                    if (!foundNode) continue;

                    nodes.Add(node);
                    break;
                }
            }

            //If any node is selected, clear the current selection and add these
            //nodes to current selection. When nothing is selected, DONOT clear
            //the selection because it may end up removing a manipulator while
            //selecting it's gizmos.
            if(nodes.Any())
            {
                DynamoSelection.Instance.ClearSelection();
                nodes.ForEach(x => vm.Model.AddToSelection(x));
            }
        }

        public override void HighlightNodeGraphics(IEnumerable<NodeModel> nodes)
        {
            HighlightGraphicsOnOff(nodes, true);
        }

        public override void UnHighlightNodeGraphics(IEnumerable<NodeModel> nodes)
        {
            HighlightGraphicsOnOff(nodes, false);
        }

        private void HighlightGraphicsOnOff(IEnumerable<NodeModel> nodes, bool highlightOn)
        {
            foreach (var node in nodes)
            {
                var geometries = FindAllGeometryModel3DsForNode(node.AstIdentifierBase);
                foreach (var geometry in geometries)
                {
                    var pointGeom = geometry.Value as PointGeometryModel3D;
                    
                    if (pointGeom == null) continue;
                    
                    var points = pointGeom.Geometry;
                    points.Colors.Clear();
                    
                    points.Colors.AddRange(highlightOn
                        ? Enumerable.Repeat(highlightColor, points.Positions.Count)
                        : Enumerable.Repeat(defaultPointColor, points.Positions.Count));

                    pointGeom.Size = highlightOn ? highlightSize : defaultPointSize;

                    pointGeom.Detach();
                    OnRequestAttachToScene(pointGeom);
                }
            }
        }

        private void CreateOrUpdateText(string baseId, Vector3 pt, IRenderPackage rp)
        {
            var textId = baseId + TextKey;
            BillboardTextModel3D bbText;
            if (Model3DDictionary.ContainsKey(textId))
            {
                bbText = Model3DDictionary[textId] as BillboardTextModel3D;
            }
            else
            {
                bbText = new BillboardTextModel3D()
                {
                    Geometry = HelixRenderPackage.InitText3D(),
                };
                Model3DDictionary.Add(textId, bbText);
            }
            var geom = bbText.Geometry as BillboardText3D;
            geom.TextInfo.Add(new TextInfo(HelixRenderPackage.CleanTag(rp.Description),
                new Vector3(pt.X + 0.025f, pt.Y + 0.025f, pt.Z + 0.025f)));
        }

        private DynamoGeometryModel3D CreateDynamoGeometryModel3D(HelixRenderPackage rp)
        {
            var meshGeometry3D = new DynamoGeometryModel3D(renderTechnique)
            {
                Transform = Model1Transform,
                Material = WhiteMaterial,
                IsHitTestVisible = false,
                RequiresPerVertexColoration = rp.RequiresPerVertexColoration,
                IsSelected = rp.IsSelected
            };

            if (rp.Colors != null)
            {
                var pf = PixelFormats.Bgra32;
                var stride = (rp.ColorsStride / 4 * pf.BitsPerPixel + 7) / 8;
                try
                {
                    var diffMap = BitmapSource.Create(rp.ColorsStride / 4, rp.Colors.Count() / rp.ColorsStride, 96.0, 96.0, pf, null,
                        rp.Colors.ToArray(), stride);
                    var diffMat = new PhongMaterial
                    {
                        Name = "White",
                        AmbientColor = PhongMaterials.ToColor(0.1, 0.1, 0.1, 1.0),
                        DiffuseColor = defaultMaterialColor,
                        SpecularColor = PhongMaterials.ToColor(0.0225, 0.0225, 0.0225, 1.0),
                        EmissiveColor = PhongMaterials.ToColor(0.0, 0.0, 0.0, 1.0),
                        SpecularShininess = 12.8f,
                        DiffuseMap = diffMap
                    };
                    meshGeometry3D.Material = diffMat;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);
                }
            }

            return meshGeometry3D;
        }

        private DynamoLineGeometryModel3D CreateLineGeometryModel3D(HelixRenderPackage rp, double thickness = 1.0)
        {
            var lineGeometry3D = new DynamoLineGeometryModel3D()
            {
                Geometry = HelixRenderPackage.InitLineGeometry(),
                Transform = Model1Transform,
                Color = Color.White,
                Thickness = thickness,
                IsHitTestVisible = false,
                IsSelected = rp.IsSelected
            };
            return lineGeometry3D;
        }

        private DynamoPointGeometryModel3D CreatePointGeometryModel3D(HelixRenderPackage rp)
        {
            var pointGeometry3D = new DynamoPointGeometryModel3D
            {
                Geometry = HelixRenderPackage.InitPointGeometry(),
                Transform = Model1Transform,
                Color = Color.White,
                Figure = PointGeometryModel3D.PointFigure.Ellipse,
                Size = defaultPointSize,
                IsHitTestVisible = true,
                IsSelected = rp.IsSelected
            };
            return pointGeometry3D;
        }

        private void AttachAllGeometryModel3DToRenderHost()
        {
            foreach (var model3D in Model3DDictionary.Select(kvp => kvp.Value))
            {
                OnRequestAttachToScene(model3D);
            }
        }

        private static MeshGeometry3D DrawTestMesh()
        {
            var b1 = new MeshBuilder();
            for (var x = 0; x < 4; x++)
            {
                for (var y = 0; y < 4; y++)
                {
                    for (var z = 0; z < 4; z++)
                    {
                        b1.AddBox(new Vector3(x, y, z), 0.5, 0.5, 0.5, BoxFaces.All);
                    }
                }
            }
            var mesh = b1.ToMeshGeometry3D();

            mesh.Colors = new Color4Collection();
            foreach (var v in mesh.Positions)
            {
                mesh.Colors.Add(new Color4(1f, 0f, 0f, 1f));
            }

            return mesh;
        }

        internal void UpdateNearClipPlane()
        {
            var near = camera.NearPlaneDistance;
            var far = camera.FarPlaneDistance;

            ComputeClipPlaneDistances(camera.Position.ToVector3(), camera.LookDirection.ToVector3(), SceneItems,
                NearPlaneDistanceFactor, out near, out far, DefaultNearClipDistance, DefaultFarClipDistance);

            if (Camera.NearPlaneDistance == near && Camera.FarPlaneDistance == far) return;

            Camera.NearPlaneDistance = near;
            Camera.FarPlaneDistance = far;
        }

        /// <summary>
        /// This method clamps the near and far clip planes around the scene geometry
        /// to achiever higher z-buffer precision.
        /// 
        /// It does this by finding the distance from each GeometryModel3D object's corner points
        /// to the camera plane. The camera's far clip plane is set to 2 * dfar, and the camera's 
        /// near clip plane is set to nearPlaneDistanceFactor * dnear
        /// </summary>
        internal static void ComputeClipPlaneDistances(Vector3 cameraPosition, Vector3 cameraLook, IEnumerable<Model3D> geometry, 
            double nearPlaneDistanceFactor, out double near, out double far, double defaultNearClipDistance, double defaultFarClipDistance)
        {
            near = defaultNearClipDistance;
            far = DefaultFarClipDistance;

            var validGeometry = geometry.Where(i => i is GeometryModel3D).ToArray();
            if (!validGeometry.Any()) return;

            var bounds = validGeometry.Cast<GeometryModel3D>().Select(g=>g.Bounds());

            // See http://mathworld.wolfram.com/Point-PlaneDistance.html
            // The plane distance formula will return positive values for points on the same side of the plane
            // as the plane's normal, and negative values for points on the opposite side of the plane. 

            var distances = bounds.SelectMany(b => b.GetCorners()).
                Select(c => c.DistanceToPlane(cameraPosition, cameraLook.Normalized())).
                ToList();

            if (!distances.Any()) return;

            distances.Sort();

            // All behind
            // Set the near and far clip to their defaults
            // because nothing is in front of the camera.
            if (distances.All(d => d < 0))
            {
                near = defaultNearClipDistance;
                far = defaultFarClipDistance;
                return;
            }

            // All in front or some in front and some behind
            // Set the near clip plane to some fraction of the 
            // of the distance to the first point.
            var closest = distances.First(d => d >= 0);
            near = closest.AlmostEqualTo(0, EqualityTolerance) ? DefaultNearClipDistance : Math.Max(DefaultNearClipDistance, closest * nearPlaneDistanceFactor);
            far = distances.Last() * 2;

        }

        internal static IEnumerable<string> FindIdentifiersForSelectedNodes(IEnumerable<NodeModel> selectedNodes)
        {
            return selectedNodes.SelectMany(n => n.OutPorts.Select(p => n.GetAstIdentifierForOutputIndex(p.Index).Value));
        }

        /// <summary>
        /// Find all output identifiers for all custom nodes in the provided workspace. 
        /// </summary>
        /// <param name="workspace">A workspace</param>
        /// <returns>An <see cref="IEnumerable"/> of <see cref="string"/> containing all output identifiers for 
        /// all custom nodes in the provided workspace, or null if the workspace is null.</returns>
        internal static IEnumerable<string> FindIdentifiersForCustomNodes(HomeWorkspaceModel workspace)
        {
            if (workspace == null)
            {
                return null;
            }

            // Remove the output identifier appended to the custom node outputs.
            var rgx = new Regex("_out[0-9]");

            var customNodes = workspace.Nodes.Where(n => n is Function);
            var idents = new List<string>();
            foreach (var n in customNodes)
            {
                if (n.IsPartiallyApplied)
                {
                    // Find output identifiers for the connected map node
                    var mapOutportsIdents =
                        n.OutPorts.SelectMany(
                            np => np.Connectors.SelectMany(
                                    c => c.End.Owner.OutPorts.Select(
                                            mp => rgx.Replace(mp.Owner.GetAstIdentifierForOutputIndex(mp.Index).Value, ""))));
                    
                    idents.AddRange(mapOutportsIdents);
                }
                else
                {
                    idents.AddRange(n.OutPorts.Select(p => rgx.Replace(n.GetAstIdentifierForOutputIndex(p.Index).Value, "")));
                }
            }
            return idents;
        }

        internal static IEnumerable<string> AllOutputIdentifiersInWorkspace(HomeWorkspaceModel workspace)
        {
            if (workspace == null)
            {
                return null;
            }

            return
                workspace.Nodes
                    .SelectMany(n => n.OutPorts.Select(p => n.GetAstIdentifierForOutputIndex(p.Index).Value));
        } 

        internal static IEnumerable<GeometryModel3D> FindGeometryForIdentifiers(IEnumerable<GeometryModel3D> geometry, IEnumerable<string> identifiers)
        {
            return identifiers.SelectMany(id => geometry.Where(item => item.Name.Contains(id))).ToArray();
        }

        /// <summary>
        /// For a set of selected nodes, compute a bounding box which
        /// encompasses all of the nodes' generated geometry.
        /// </summary>
        /// <param name="geometry">A collection of <see cref="GeometryModel3D"/> objects.</param>
        /// <returns>A <see cref="BoundingBox"/> object.</returns>
        internal static BoundingBox ComputeBoundsForGeometry(GeometryModel3D[] geometry)
        {
            if (!geometry.Any()) return DefaultBounds;

            var bounds = geometry.First().Bounds();
            bounds = geometry.Aggregate(bounds, (current, geom) => BoundingBox.Merge(current, geom.Bounds()));

#if DEBUG
            Debug.WriteLine("{0} geometry items referenced by the selection.", geometry.Count());
            Debug.WriteLine("Bounding box of selected geometry:{0}", bounds);
#endif
            return bounds;
        }

        internal override void ExportToSTL(string path, string modelName)
        {
            var geoms = SceneItems.Where(i => i is DynamoGeometryModel3D).
                Cast<DynamoGeometryModel3D>();

            using (TextWriter tw = new StreamWriter(path))
            {
                tw.WriteLine("solid {0}", model.CurrentWorkspace.Name);
                foreach (var g in geoms)
                {
                    var n = ((MeshGeometry3D) g.Geometry).Normals.ToList();
                    var t = ((MeshGeometry3D)g.Geometry).Triangles.ToList();

                    for (var i = 0; i < t.Count(); i ++)
                    {
                        var nCount = i*3;
                        tw.WriteLine("\tfacet normal {0} {1} {2}", n[nCount].X, n[nCount].Y, n[nCount].Z);
                        tw.WriteLine("\t\touter loop");
                        tw.WriteLine("\t\t\tvertex {0} {1} {2}", t[i].P0.X, t[i].P0.Y, t[i].P0.Z);
                        tw.WriteLine("\t\t\tvertex {0} {1} {2}", t[i].P1.X, t[i].P1.Y, t[i].P1.Z);
                        tw.WriteLine("\t\t\tvertex {0} {1} {2}", t[i].P2.X, t[i].P2.Y, t[i].P2.Z);
                        tw.WriteLine("\t\tendloop");
                        tw.WriteLine("\tendfacet");
                    }
                }
                tw.WriteLine("endsolid {0}", model.CurrentWorkspace.Name);
            }
        }

        #endregion
    }

    /// <summary>
    /// The Model3DComparer is used to sort arrays of Model3D objects. 
    /// After sorting, the target array's objects will be organized
    /// as follows:
    /// 1. All opaque geometry.
    /// 2. All text.
    /// 3. All transparent geometry, ordered by distance from
    /// the camera.
    /// </summary>
    public class Model3DComparer : IComparer<Model3D>
    {
        private readonly Vector3 cameraPosition;

        public Model3DComparer(Point3D cameraPosition)
        {
            this.cameraPosition = cameraPosition.ToVector3();
        }

        public int Compare(Model3D x, Model3D y)
        {
            var a = x as GeometryModel3D;
            var b = y as GeometryModel3D;

            if (a == null && b == null)
            {
                return 0;
            }

            if (a == null)
            {
                return -1;
            }

            if (b == null)
            {
                return 1;
            }

            var textA = a.GetType() == typeof(BillboardTextModel3D);
            var textB = b.GetType() == typeof(BillboardTextModel3D);
            var result = textA.CompareTo(textB);

            if (result == 0 && textA)
            {
                return result;
            }

            var transA = (bool) a.GetValue(AttachedProperties.HasTransparencyProperty);
            var transB = (bool) b.GetValue(AttachedProperties.HasTransparencyProperty);
            result = transA.CompareTo(transB);

            if (result != 0 || !transA) return result;

            // compare distance
            var boundsA = a.Bounds;
            var boundsB = b.Bounds;
            var cpA = (boundsA.Maximum + boundsA.Minimum)/2;
            var cpB = (boundsB.Maximum + boundsB.Minimum)/2;
            var dA = Vector3.DistanceSquared(cpA, cameraPosition);
            var dB = Vector3.DistanceSquared(cpB, cameraPosition);
            return -dA.CompareTo(dB);
        }
    }

    internal static class CameraExtensions
    {
        public static CameraData ToCameraData(this PerspectiveCamera camera, string name)
        {
            var camData = new CameraData
            {
                Name = name,
                LookDirection = camera.LookDirection,
                EyePosition = camera.Position,
                UpDirection = camera.UpDirection,
                NearPlaneDistance = camera.NearPlaneDistance,
                FarPlaneDistance = camera.FarPlaneDistance
            };

            return camData;
        }
    }

    internal static class BoundingBoxExtensions
    {
        /// <summary>
        /// Convert a <see cref="BoundingBox"/> to a <see cref="Rect3D"/>
        /// </summary>
        /// <param name="bounds">The <see cref="BoundingBox"/> to be converted.</param>
        /// <returns>A <see cref="Rect3D"/> object.</returns>
        internal static Rect3D ToRect3D(this BoundingBox bounds)
        {
            var min = bounds.Minimum;
            var max = bounds.Maximum;
            var size = new Size3D((max.X - min.X), (max.Y - min.Y), (max.Z - min.Z));
            return new Rect3D(min.ToPoint3D(), size);
        }

        /// <summary>
        /// If a <see cref="GeometryModel3D"/> has more than one point, then
        /// return its bounds, otherwise, return a bounding
        /// box surrounding the point of the supplied size.
        /// 
        /// This extension method is to correct for the Helix toolkit's GeometryModel3D.Bounds
        /// property which does not update correctly as new geometry is added to the GeometryModel3D.
        /// </summary>
        /// <param name="pointGeom">A <see cref="GeometryModel3D"/> object.</param>
        /// <returns>A <see cref="BoundingBox"/> object encapsulating the geometry.</returns>
        internal static BoundingBox Bounds(this GeometryModel3D geom, float defaultBoundsSize = 5.0f)
        {
            if (geom.Geometry.Positions.Count == 0)
            {
                return new BoundingBox();
            }

            if (geom.Geometry.Positions.Count > 1)
            {
                return BoundingBox.FromPoints(geom.Geometry.Positions.ToArray());
            }

            var pos = geom.Geometry.Positions.First();
            var min = pos + new Vector3(-defaultBoundsSize, -defaultBoundsSize, -defaultBoundsSize);
            var max = pos + new Vector3(defaultBoundsSize, defaultBoundsSize, defaultBoundsSize);
            return new BoundingBox(min, max);
        }

        public static Vector3 Center(this BoundingBox bounds)
        {
            return (bounds.Maximum + bounds.Minimum)/2;
        }

    }

    internal static class Vector3Extensions
    {
        internal static double DistanceToPlane(this Vector3 point, Vector3 planeOrigin, Vector3 planeNormal)
        {
            return Vector3.Dot(planeNormal, (point - planeOrigin));
        }
    }

    internal static class DoubleExtensions
    {
        internal static bool AlmostEqualTo(this double a, double b, double tolerance)
        {
            return Math.Abs(a - b) < tolerance;
        }
    }
}
