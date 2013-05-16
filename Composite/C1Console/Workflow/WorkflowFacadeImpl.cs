﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Workflow.Activities;
using System.Workflow.ComponentModel.Compiler;
using System.Workflow.Runtime;
using System.Workflow.Runtime.Hosting;
using System.Xml.Linq;
using Composite.C1Console.Actions;
using Composite.C1Console.Events;
using Composite.C1Console.Security;
using Composite.C1Console.Tasks;
using Composite.C1Console.Workflow.Activities.Foundation;
using Composite.C1Console.Workflow.Foundation;
using Composite.C1Console.Workflow.Foundation.PluginFacades;
using Composite.Core;
using Composite.Core.Collections.Generic;
using Composite.Core.Configuration;
using Composite.Core.IO;
using Composite.Core.Threading;
using Composite.Core.Types;
using Composite.Core.Xml;
using Composite.Data;


namespace Composite.C1Console.Workflow
{
    internal sealed class WorkflowFacadeImpl : IWorkflowFacade
    {
        private static readonly string LogTitle = "WorkflowFacade";
        private static readonly string LogTitleColored = "RGB(194, 252, 131)" + LogTitle;

        private static readonly TimeSpan _oldFileExistensTimeout = TimeSpan.FromDays(30.0);

        private Thread _initializeThread;
        private readonly object _initializeThreadLock = new object();
        private bool _isShutDown;
        private WorkflowRuntime _workflowRuntime;
        private readonly List<Action> _actionToRunWhenInitialized = new List<Action>();

        private ExternalDataExchangeService _externalDataExchangeService;
        private FormsWorkflowEventService _formsWorkflowEventService;
        private ManualWorkflowSchedulerService _manualWorkflowSchedulerService;
        private FileWorkFlowPersisetenceService _fileWorkFlowPersistenceService;

        private readonly ResourceLocker<Resources> _resourceLocker = new ResourceLocker<Resources>(new Resources(), Resources.InitializeResources);

        private readonly Dictionary<Type, bool> _hasEntityTokenLockAttributeCache = new Dictionary<Type, bool>();


        public WorkflowFacadeImpl()
        {
            string serializedWorflowsDirectory = PathUtil.Resolve(GlobalSettingsFacade.SerializedWorkflowsDirectory);
            string parentDirectory = Path.GetDirectoryName(serializedWorflowsDirectory);
            string lockFileDirectory = Path.Combine(parentDirectory, "LockFiles");

            if (!C1Directory.Exists(lockFileDirectory)) C1Directory.CreateDirectory(lockFileDirectory);
        }


        public void EnsureInitialization()
        {
            if (_initializeThread != null) return;

            lock (_initializeThreadLock)
            {
                if (_initializeThread != null) return;
                
                ThreadStart threadStart = () =>
                    {
                        using(ThreadDataManager.EnsureInitialize()) 
                        {
                            int startTime = Environment.TickCount;
                            while (_workflowRuntime == null && !_isShutDown && startTime + 30000 > Environment.TickCount)
                            {
                                Thread.Sleep(100);
                            }

                            if (_workflowRuntime != null)
                            {
                                Log.LogVerbose(LogTitleColored, "Already initialized, skipping delayed initialization");
                                return;
                            }

                            if (_isShutDown)
                            {
                                Log.LogVerbose(LogTitleColored, "System is shutting down, skipping delayed initialization");
                                return;
                            }

                            int endTime = Environment.TickCount;

                            using (_resourceLocker.Locker)
                            {
                                DoInitialize(endTime - startTime);
                            }
                        }
                    };

                _initializeThread = new Thread(threadStart);
                _initializeThread.Start();
                
            }
        }


        public WorkflowRuntime WorkflowRuntime
        {
            get
            {
                DoInitialize(0);

                return _workflowRuntime;
            }
        }



        public void RunWhenInitialized(Action action)
        {
            _actionToRunWhenInitialized.Add(action);
        }



        #region Workflow methods
        public WorkflowInstance CreateNewWorkflow(Type workflowType)
        {
            GlobalInitializerFacade.EnsureSystemIsInitialized();
            DoInitialize(0);

            try
            {
                WorkflowInstance workflowInstance = _workflowRuntime.CreateWorkflow(workflowType);

                SetWorkflowPersistingType(workflowType, workflowInstance.InstanceId);

                return workflowInstance;
            }
            catch (WorkflowValidationFailedException exp)
            {
                StringBuilder errors = new StringBuilder();
                foreach (ValidationError error in exp.Errors)
                {

                    errors.AppendLine(error.ToString());
                }
                Log.LogError("WorkflowFacade", errors.ToString());
                throw;
            }
        }



        public WorkflowInstance CreateNewWorkflow(Type workflowType, Dictionary<string, object> arguments)
        {
            GlobalInitializerFacade.EnsureSystemIsInitialized();
            DoInitialize(0);

            try
            {
                WorkflowInstance workflowInstance = _workflowRuntime.CreateWorkflow(workflowType, arguments);

                SetWorkflowPersistingType(workflowType, workflowInstance.InstanceId);

                if ((arguments.ContainsKey("SerializedEntityToken")) &&
                    (arguments.ContainsKey("SerializedActionToken")))
                {
                    ActionToken actionToken = ActionTokenSerializer.Deserialize((string)arguments["SerializedActionToken"]);

                    if (actionToken.IgnoreEntityTokenLocking == false)
                    {
                        AcquireLockIfNeeded(workflowType, workflowInstance.InstanceId, (string)arguments["SerializedEntityToken"]);
                    }
                }

                return workflowInstance;
            }
            catch (WorkflowValidationFailedException exp)
            {
                StringBuilder errors = new StringBuilder();
                foreach (ValidationError error in exp.Errors)
                {

                    errors.AppendLine(error.ToString());
                }
                Log.LogError("WorkflowFacade", errors.ToString());
                throw;
            }
        }



        public WorkflowFlowToken StartNewWorkflow(Type workflowType, FlowControllerServicesContainer flowControllerServicesContainer, EntityToken entityToken, ActionToken actionToken)
        {
            DoInitialize(0);

            Dictionary<string, object> arguments = new Dictionary<string, object> { { "EntityToken", entityToken }, { "ActionToken", actionToken } };

            try
            {
                WorkflowInstance workflowInstance = _workflowRuntime.CreateWorkflow(workflowType, arguments);

                SetWorkflowPersistingType(workflowType, workflowInstance.InstanceId);
                AcquireLockIfNeeded(workflowType, workflowInstance.InstanceId, entityToken);

                workflowInstance.Start();

                SetFlowControllerServicesContainer(workflowInstance.InstanceId, flowControllerServicesContainer);

                RunWorkflow(workflowInstance);

                return new WorkflowFlowToken(workflowInstance.InstanceId);
            }
            catch (Exception e)
            {
                Log.LogCritical(LogTitle, e);

                throw;
            }
        }



        public WorkflowInstance GetWorkflow(Guid instanceId)
        {
            DoInitialize(0);

            return _workflowRuntime.GetWorkflow(instanceId);
        }



        public StateMachineWorkflowInstance GetStateMachineWorkflowInstance(Guid instanceId)
        {
            DoInitialize(0);

            return new StateMachineWorkflowInstance(_workflowRuntime, instanceId);
        }



        public void RunWorkflow(Guid instanceId)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                SetWorkflowInstanceStatus(instanceId, WorkflowInstanceStatus.Running, false);

                _manualWorkflowSchedulerService.RunWorkflow(instanceId);

                Exception exception;
                if (_resourceLocker.Resources.ExceptionFromWorkflow.TryGetValue(Thread.CurrentThread.ManagedThreadId, out exception))
                {
                    _resourceLocker.Resources.ExceptionFromWorkflow.Remove(Thread.CurrentThread.ManagedThreadId);

                    Log.LogCritical(LogTitle, exception);

                    throw exception;
                }
            }
        }



        public void RunWorkflow(WorkflowInstance workflowInstance)
        {
            RunWorkflow(workflowInstance.InstanceId);
        }


        public void AbortWorkflow(Guid instanceId)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (AbortedWorkflows.Contains(instanceId)) return;
                AbortedWorkflows.Add(instanceId);

                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    var workflowInstance = WorkflowRuntime.GetWorkflow(instanceId);

                    workflowInstance.Abort();
                    
                    Exception exception;
                    if (_resourceLocker.Resources.ExceptionFromWorkflow.TryGetValue(Thread.CurrentThread.ManagedThreadId, out exception))
                    {
                        _resourceLocker.Resources.ExceptionFromWorkflow.Remove(Thread.CurrentThread.ManagedThreadId);

                        throw exception;
                    }
                }
            }
        }



        private void SetWorkflowPersistingType(Type workflowType, Guid instanceId)
        {
            List<AllowPersistingWorkflowAttribute> attributes = workflowType.GetCustomAttributesRecursively<AllowPersistingWorkflowAttribute>().ToList();

            Verify.That(attributes.Count <= 1, "More than one attribute of type '0' found", typeof(AllowPersistingWorkflowAttribute).FullName);

            var persistanceType = attributes.Count == 1 ? attributes[0].WorkflowPersistingType : WorkflowPersistingType.Never;

            using (_resourceLocker.Locker)
            {
                _resourceLocker.Resources.WorkflowPersistingTypeDictionary.Add(instanceId, persistanceType);
            }
        }



        private void RemovePersistingType(Guid instanceId)
        {
            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowPersistingTypeDictionary.ContainsKey(instanceId))
                {
                    _resourceLocker.Resources.WorkflowPersistingTypeDictionary.Remove(instanceId);
                }
            }
        }



        private void AcquireLockIfNeeded(Type workflowType, Guid instanceId, string serializedEntityToken)
        {
            if (HasEntityTokenLockAttribute(workflowType))
            {
                EntityToken entityToken = EntityTokenSerializer.Deserialize(serializedEntityToken);

                AcquireLock(instanceId, entityToken);
            }
        }



        private void AcquireLockIfNeeded(Type workflowType, Guid instanceId, EntityToken entityToken)
        {
            if (HasEntityTokenLockAttribute(workflowType))
            {
                AcquireLock(instanceId, entityToken);
            }
        }



        public void AcquireLock(Guid isntanceId, EntityToken entityToken)
        {
            Verify.That(!ActionLockingFacade.IsLocked(entityToken), "The entityToken is already locked");

            ActionLockingFacade.AcquireLock(entityToken, isntanceId);
        }



        private void ReleaseAllLocks(Guid instanceId)
        {
            ActionLockingFacade.ReleaseAllLocks(instanceId);
        }



        private bool HasEntityTokenLockAttribute(Type workflowType)
        {
            bool hasEntityLockAttribute;
            if (_hasEntityTokenLockAttributeCache.TryGetValue(workflowType, out hasEntityLockAttribute) == false)
            {
                hasEntityLockAttribute = workflowType.GetCustomAttributesRecursively<EntityTokenLockAttribute>().Any() ;

                _hasEntityTokenLockAttributeCache.Add(workflowType, hasEntityLockAttribute);
            }

            return hasEntityLockAttribute;
        }
        #endregion



        #region FlowControllerServices methods
        public void SetFlowControllerServicesContainer(Guid instanceId, FlowControllerServicesContainer flowControllerServicesContainer)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.FlowControllerServicesContainers.ContainsKey(instanceId) == false)
                {
                    _resourceLocker.Resources.FlowControllerServicesContainers.Add(instanceId, flowControllerServicesContainer);
                }
                else
                {
                    _resourceLocker.Resources.FlowControllerServicesContainers[instanceId] = flowControllerServicesContainer;
                }
            }
        }



        public FlowControllerServicesContainer GetFlowControllerServicesContainer(Guid instanceId)
        {
            DoInitialize(0);

            FlowControllerServicesContainer flowControllerServicesContainer;

            using (_resourceLocker.Locker)
            {
                _resourceLocker.Resources.FlowControllerServicesContainers.TryGetValue(instanceId, out flowControllerServicesContainer);
            }

            return flowControllerServicesContainer;
        }



        public void RemoveFlowControllerServicesContainer(Guid instanceId)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.FlowControllerServicesContainers.ContainsKey(instanceId))
                {
                    _resourceLocker.Resources.FlowControllerServicesContainers.Remove(instanceId);
                }
            }
        }
        #endregion



        #region Workflow status methods
        public bool WorkflowExists(Guid instanceId)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                return _resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId);
            }
        }



        public Semaphore WaitForIdleStatus(Guid instanceId)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                WorkflowInstanceStatus workflowInstanceStatus;
                if (_resourceLocker.Resources.WorkflowStatusDictionary.TryGetValue(instanceId, out workflowInstanceStatus) == false)
                {
                    throw new InvalidOperationException(string.Format("The workflow with the id '{0}' is unknown", instanceId));
                }

                if (workflowInstanceStatus == WorkflowInstanceStatus.Idle)
                {
                    return null;
                }
                
                if (_resourceLocker.Resources.WorkflowIdleWaitSemaphoes.ContainsKey(instanceId))
                {
                    _resourceLocker.Resources.WorkflowIdleWaitSemaphoes.Remove(instanceId);
                }

                Semaphore semaphore = new Semaphore(0, 1);
                _resourceLocker.Resources.WorkflowIdleWaitSemaphoes.Add(instanceId, semaphore);
                return semaphore;
            }
        }



        private void SetWorkflowInstanceStatus(Guid instanceId, WorkflowInstanceStatus workflowInstanceStatus, bool newlyCreateOrLoaded)
        {
            using (_resourceLocker.Locker)
            {
                switch (workflowInstanceStatus)
                {
                    case WorkflowInstanceStatus.Idle:
                        if (_resourceLocker.Resources.WorkflowIdleWaitSemaphoes.ContainsKey(instanceId))
                        {
                            _resourceLocker.Resources.WorkflowIdleWaitSemaphoes[instanceId].Release();
                            _resourceLocker.Resources.WorkflowIdleWaitSemaphoes.Remove(instanceId); ;
                        }

                        if ((_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId) == false) && (newlyCreateOrLoaded))
                        {
                            _resourceLocker.Resources.WorkflowStatusDictionary.Add(instanceId, WorkflowInstanceStatus.Idle);
                        }

                        _resourceLocker.Resources.WorkflowStatusDictionary[instanceId] = WorkflowInstanceStatus.Idle;                        

                        Log.LogVerbose(LogTitle, "Workflow instance status changed to idle. Id = {0}", instanceId);

                        PersistFormData(instanceId);

                        break;

                    case WorkflowInstanceStatus.Running:
                        _resourceLocker.Resources.WorkflowStatusDictionary[instanceId] = WorkflowInstanceStatus.Running;

                        Log.LogVerbose(LogTitle, "Workflow instance status changed to running. Id = {0}", instanceId);
                        break;

                    case WorkflowInstanceStatus.Terminated:
                        if (_resourceLocker.Resources.WorkflowIdleWaitSemaphoes.ContainsKey(instanceId))
                        {
                            _resourceLocker.Resources.WorkflowIdleWaitSemaphoes[instanceId].Release();
                            _resourceLocker.Resources.WorkflowIdleWaitSemaphoes.Remove(instanceId); 
                        }

                        _resourceLocker.Resources.WorkflowStatusDictionary.Remove(instanceId);

                        Log.LogVerbose(LogTitle, "Workflow instance status changed to terminated. Id = {0}", instanceId);
                        break;
                }
            }
        }
        #endregion



        #region Form workflow methods
        public void SetEventHandlerFilter(Guid instanceId, Type eventHandlerFilterType)
        {
            DoInitialize(0);

            if (eventHandlerFilterType != null)
            {
                if (typeof(IEventHandleFilter).IsAssignableFrom(eventHandlerFilterType) == false) throw new ArgumentException(string.Format("The argument eventHandlerFilterType does dot implement the interface '{0}'", typeof(IEventHandleFilter)));

                FormData formData = GetFormData(instanceId);
                if (formData != null)
                {
                    formData.EventHandleFilterType = eventHandlerFilterType;
                }
            }
        }



        public IEventHandleFilter GetEventHandleFilter(Guid instanceId)
        {
            DoInitialize(0);

            FormData formData = GetFormData(instanceId);

            if ((formData == null) || (formData.EventHandleFilterType == null)) return null;

            IEventHandleFilter eventHandleFilter;
            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.EventHandleFilters.TryGetValue(formData.EventHandleFilterType, out eventHandleFilter) == false)
                {
                    eventHandleFilter = (IEventHandleFilter)Activator.CreateInstance(formData.EventHandleFilterType);
                    _resourceLocker.Resources.EventHandleFilters.Add(formData.EventHandleFilterType, eventHandleFilter);
                }
            }

            return eventHandleFilter;
        }



        public IEnumerable<string> GetCurrentFormEvents(Guid instanceId)
        {
            DoInitialize(0);

            IEnumerable<string> eventNames = new StateMachineWorkflowInstance(WorkflowFacade.WorkflowRuntime, instanceId).GetCurrentEventNames(typeof(IFormsWorkflowEventService));

            return eventNames;
        }



        public IEnumerable<string> GetCurrentFormEvents(WorkflowInstance workflowInstance)
        {
            DoInitialize(0);

            return GetCurrentFormEvents(workflowInstance.InstanceId);
        }



        public void FireSaveEvent(Guid instanceId, Dictionary<string, object> bindings)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    _formsWorkflowEventService.FireSaveEvent(new FormEventArgs(instanceId, bindings));
                }
            }
        }



        public void FireSaveAndPublishEvent(Guid instanceId, Dictionary<string, object> bindings)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    _formsWorkflowEventService.FireSaveAndPublishEvent(new FormEventArgs(instanceId, bindings));
                }
            }
        }



        public void FireNextEvent(Guid instanceId, Dictionary<string, object> bindings)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    _formsWorkflowEventService.FireNextEvent(new FormEventArgs(instanceId, bindings));
                }
            }
        }



        public void FirePreviousEvent(Guid instanceId, Dictionary<string, object> bindings)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    _formsWorkflowEventService.FirePreviousEvent(new FormEventArgs(instanceId, bindings));
                }
            }
        }



        public void FireFinishEvent(Guid instanceId, Dictionary<string, object> bindings)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    _formsWorkflowEventService.FireFinishEvent(new FormEventArgs(instanceId, bindings));
                }
            }
        }



        public void FireCancelEvent(Guid instanceId, Dictionary<string, object> bindings)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    _formsWorkflowEventService.FireCancelEvent(new FormEventArgs(instanceId, bindings));
                }
            }
        }



        public void FirePreviewEvent(Guid instanceId, Dictionary<string, object> bindings)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    _formsWorkflowEventService.FirePreviewEvent(new FormEventArgs(instanceId, bindings));
                }
            }
        }



        public void FireCustomEvent(int customEventNumber, Guid instanceId, Dictionary<string, object> bindings)
        {
            DoInitialize(0);

            if (customEventNumber < 1 || customEventNumber > 5) throw new ArgumentException("Number must be between 1 and 5", "customEventNumber");

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                {
                    switch (customEventNumber)
                    {
                        case 01:
                            _formsWorkflowEventService.FireCustomEvent01(new FormEventArgs(instanceId, bindings));
                            break;
                        case 02:
                            _formsWorkflowEventService.FireCustomEvent02(new FormEventArgs(instanceId, bindings));
                            break;
                        case 03:
                            _formsWorkflowEventService.FireCustomEvent03(new FormEventArgs(instanceId, bindings));
                            break;
                        case 04:
                            _formsWorkflowEventService.FireCustomEvent04(new FormEventArgs(instanceId, bindings));
                            break;
                        case 05:
                            _formsWorkflowEventService.FireCustomEvent05(new FormEventArgs(instanceId, bindings));
                            break;
                        default:
                            break;
                    }
                }
            }
        }



        public void FireChildWorkflowDoneEvent(Guid parentInstanceId, string workflowResult)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(parentInstanceId))
                {
                    _formsWorkflowEventService.FireChildWorkflowDoneEvent(new FormEventArgs(parentInstanceId, workflowResult));
                }
            }
        }
        #endregion



        #region FormData methods
        public void AddFormData(Guid instanceId, FormData formData)
        {
            using (_resourceLocker.Locker)
            {
                _resourceLocker.Resources.FormData.Add(instanceId, formData);
            }
        }



        public bool TryGetFormData(Guid instanceId, out FormData formData)
        {
            using (_resourceLocker.Locker)
            {
                formData = null;

                if (_resourceLocker.Resources.FormData.TryGetValue(instanceId, out formData))
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }



        public FormData GetFormData(Guid instanceId, bool allowCreationIfNotExisting = false)
        {
            FormData formData;
            if (!TryGetFormData(instanceId, out formData) && allowCreationIfNotExisting)
            {
                formData = new FormData();
                AddFormData(instanceId, formData);
            }

            return formData;
        }



        private void RemoveIfExistFormData(Guid instanceId)
        {
            using (_resourceLocker.Locker)
            {
                if (_resourceLocker.Resources.FormData.ContainsKey(instanceId))
                {
                    _resourceLocker.Resources.FormData.Remove(instanceId);
                }
            }
        }
        #endregion



        public void Flush()
        {
            _workflowRuntime = null;
            _externalDataExchangeService = null;
            _manualWorkflowSchedulerService = null;
            _fileWorkFlowPersistenceService = null;
            _formsWorkflowEventService = null;

            _resourceLocker.ResetInitialization();
        }



        public void ShutDown()
        {
            _isShutDown = true;

            Log.LogVerbose(LogTitleColored, "----------========== Finalizing Workflows ==========----------");
            int startTime = Environment.TickCount;

            while (_workflowRuntime == null && Environment.TickCount - startTime < 5000)
                Thread.Sleep(10);

            if (_workflowRuntime != null)
            {
                // system shut down - close all console bound resources
                using (_resourceLocker.Locker)
                {
                    using (GlobalInitializerFacade.CoreIsInitializedScope)
                    {
                        PersistFormData();

                        UnloadWorkflowsSilent();

                        RemoveNotPersistableWorkflowsState();
                    }
                }
            }

            _workflowRuntime = null;

            int endTime = Environment.TickCount;
            Log.LogVerbose(LogTitleColored, "----------========== Done finalizing Workflows ({0} ms ) ==========----------", endTime - startTime);
        }



        public void ConsoleClosed(ConsoleClosedEventArgs args)
        {
            DoInitialize(0);

            using (_resourceLocker.Locker)
            {
                List<Guid> workflowsToCancel =
                    (from kp in _resourceLocker.Resources.FlowControllerServicesContainers
                     where ConsoleIdEquals(kp.Value, args.ConsoleId)
                     select kp.Key).ToList();

                foreach (Guid instanceId in workflowsToCancel)
                {
                    try
                    {
                        AbortWorkflow(instanceId);
                    }
                    catch(Exception ex)
                    {
                        Log.LogError(LogTitle, "Error aborting workflow " + instanceId);
                        Log.LogError(LogTitle, ex);
                    }
                }
            }
        }



        private void DoInitialize(int delayedTime)
        {
            if (_workflowRuntime == null)
            {
                using (_resourceLocker.Locker)
                {
                    if (_workflowRuntime == null)
                    {
                        Log.LogVerbose(LogTitleColored, "----------========== Initializing Workflows (Delayed: {0}) ==========----------", delayedTime);
                        int startTime = Environment.TickCount;

                        _resourceLocker.ResetInitialization();

                        InitializeWorkflowRuntime();

                        InitializeFormsWorkflowRuntime();

                        if (_workflowRuntime.IsStarted == false)
                        {
                            _workflowRuntime.StartRuntime();
                        }

                        DeleteOldWorkflows();

                        LoadPersistedWorkflows();
                        LoadPerssistedFormDatas();

                        int endTime = Environment.TickCount;
                        Log.LogVerbose(LogTitleColored, "----------========== Done initializing Workflows ({0} ms ) ==========----------", endTime - startTime);

                        foreach (Action action in _actionToRunWhenInitialized)
                        {
                            action();
                        }
                    }
                }
            }
        }



        private void InitializeWorkflowRuntime()
        {
            if (WorkflowRuntimeProviderPluginFacade.HasConfiguration)
            {
                string providerName = WorkflowRuntimeProviderRegistry.DefaultWorkflowRuntimeProviderName;

                _workflowRuntime = WorkflowRuntimeProviderPluginFacade.GetWorkflowRuntime(providerName);
            }
            else
            {
                Log.LogVerbose(LogTitle, "Using default workflow runtime");
                _workflowRuntime = new WorkflowRuntime();
            }


            _manualWorkflowSchedulerService = new ManualWorkflowSchedulerService(true);
            _workflowRuntime.AddService(_manualWorkflowSchedulerService);

            _fileWorkFlowPersistenceService = new FileWorkFlowPersisetenceService(SerializedWorkflowsDirectory);
            _workflowRuntime.AddService(_fileWorkFlowPersistenceService);

            _externalDataExchangeService = new ExternalDataExchangeService();
            _workflowRuntime.AddService(_externalDataExchangeService);


            AddWorkflowLoggingEvents();


            _workflowRuntime.WorkflowCompleted += delegate(object sender, WorkflowCompletedEventArgs args)
            {
                using (ThreadDataManager.EnsureInitialize())
                {
                    OnWorkflowInstanceTerminatedCleanup(args.WorkflowInstance.InstanceId);
                }
            };



            _workflowRuntime.WorkflowAborted += delegate(object sender, WorkflowEventArgs args)
            {
                using (ThreadDataManager.EnsureInitialize())
                {
                    OnWorkflowInstanceTerminatedCleanup(args.WorkflowInstance.InstanceId);
                }
            };



            _workflowRuntime.WorkflowTerminated += delegate(object sender, WorkflowTerminatedEventArgs args)
            {
                using (ThreadDataManager.EnsureInitialize())
                {
                    OnWorkflowInstanceTerminatedCleanup(args.WorkflowInstance.InstanceId);
                }

                using (_resourceLocker.Locker)
                {
                    _resourceLocker.Resources.ExceptionFromWorkflow.Add(Thread.CurrentThread.ManagedThreadId, args.Exception);
                }
            };



            _workflowRuntime.WorkflowCreated += delegate(object sender, WorkflowEventArgs args)
            {
                SetWorkflowInstanceStatus(args.WorkflowInstance.InstanceId, WorkflowInstanceStatus.Idle, true);
            };



            _workflowRuntime.WorkflowIdled += delegate(object sender, WorkflowEventArgs args)
            {
                SetWorkflowInstanceStatus(args.WorkflowInstance.InstanceId, WorkflowInstanceStatus.Idle, false);
            };



            _workflowRuntime.WorkflowLoaded += delegate(object sender, WorkflowEventArgs args)
            {
                SetWorkflowInstanceStatus(args.WorkflowInstance.InstanceId, WorkflowInstanceStatus.Idle, true);
            };
        }



        private void OnWorkflowInstanceTerminatedCleanup(Guid instanceId)
        {
            AbortedWorkflows.Remove(instanceId);

            WorkflowFlowToken flowToken = new WorkflowFlowToken(instanceId);

            TaskManagerFacade.CompleteTasks(flowToken);

            ReleaseAllLocks(instanceId);

            SetWorkflowInstanceStatus(instanceId, WorkflowInstanceStatus.Terminated, false);

            RemoveFlowControllerServicesContainer(instanceId);

            RemoveIfExistFormData(instanceId);

            RemovePersistingType(instanceId);

            DeletePersistedWorkflow(instanceId);

            DeletePersistedFormData(instanceId);

            using (ThreadDataManager.EnsureInitialize())
            {
                FlowControllerFacade.FlowComplete(new WorkflowFlowToken(instanceId));
            }
        }



        private void InitializeFormsWorkflowRuntime()
        {
            _formsWorkflowEventService = new FormsWorkflowEventService();
            _externalDataExchangeService.AddService(_formsWorkflowEventService);


            IFormsWorkflowActivityService formsWorkflowActivityService = new FormsWorkflowActivityService();
            _externalDataExchangeService.AddService(formsWorkflowActivityService);
        }



        [DebuggerHidden]
        private void HandleWorkflowPersistedEvent(object sender, WorkflowEventArgs args)
        {
            var instance = args.WorkflowInstance;

            try
            {
                Log.LogVerbose(LogTitle,
                    "Workflow persisted, Activity = {0}, Id = {1}", instance.GetWorkflowDefinition().GetType(), instance.InstanceId);
            }
            catch (Exception)
            {
                Log.LogVerbose(LogTitle, "Workflow persisted, Id = {0}", instance.InstanceId);
            }
        }



        [DebuggerHidden]
        private void HandleWorkflowAbortedEvent(object sender, WorkflowEventArgs args)
        {
            var instance = args.WorkflowInstance;

            try
            {
                Log.LogVerbose(LogTitle,
                    "Workflow aborted, Activity = {0}, Id = {1}", instance.GetWorkflowDefinition().GetType(), instance.InstanceId);
            }
            catch (Exception)
            {
                Log.LogVerbose(LogTitle, "Workflow aborted Id = " + instance.InstanceId);
            }
        }



        private void AddWorkflowLoggingEvents()
        {
            _workflowRuntime.WorkflowAborted += HandleWorkflowAbortedEvent;


            //_workflowRuntime.WorkflowCompleted += delegate(object sender, WorkflowCompletedEventArgs args)
            //{
            //    LoggingService.LogVerbose(
            //        "WorkflowFacade",
            //        string.Format("Workflow completed  - Id = {0}", args.WorkflowInstance.InstanceId));
            //};

            _workflowRuntime.WorkflowCreated += delegate(object sender, WorkflowEventArgs args)
            {
                Log.LogVerbose(LogTitle, "Workflow created, Activity = {1}, Id = {0}", 
                    args.WorkflowInstance.GetWorkflowDefinition().GetType(), args.WorkflowInstance.InstanceId);
            };

            //_workflowRuntime.WorkflowIdled += delegate(object sender, WorkflowEventArgs args)
            //{
            //    LoggingService.LogVerbose(
            //        "WorkflowFacade",
            //        string.Format("Workflow idled      - Id = {0}", args.WorkflowInstance.InstanceId));
            //};

            _workflowRuntime.WorkflowLoaded += delegate(object sender, WorkflowEventArgs args)
            {
                Log.LogVerbose(LogTitle, "Workflow loaded, Activity = {0}, Id = {1}", 
                    args.WorkflowInstance.GetWorkflowDefinition().GetType(), args.WorkflowInstance.InstanceId);
            };


            _workflowRuntime.WorkflowPersisted += HandleWorkflowPersistedEvent;

            /*  _workflowRuntime.WorkflowPersisted += delegate(object sender, WorkflowEventArgs args)
              {                
                  //try
                  //{
                  //    LoggingService.LogVerbose(
                  //        "WorkflowFacade",
                  //        string.Format("Workflow persisted, Activity = {0}, Id = {1}", args.WorkflowInstance.GetWorkflowDefinition().GetType(), args.WorkflowInstance.InstanceId));
                  //}
                  //catch (Exception)
                  //{
                  //    LoggingService.LogVerbose(
                  //        "WorkflowFacade",
                  //        string.Format("Workflow persisted, Id = {0}", args.WorkflowInstance.InstanceId));
                  //}                
              };*/

            //_workflowRuntime.WorkflowResumed += delegate(object sender, WorkflowEventArgs args)
            //{
            //    LoggingService.LogVerbose(
            //        "WorkflowFacade",
            //        string.Format("Workflow resumed    - Id = {0}", args.WorkflowInstance.InstanceId));
            //};

            //_workflowRuntime.WorkflowStarted += delegate(object sender, WorkflowEventArgs args)
            //{
            //    LoggingService.LogVerbose(
            //        "WorkflowFacade",
            //        string.Format("Workflow started    - Id = {0}", args.WorkflowInstance.InstanceId));
            //};

            //_workflowRuntime.WorkflowSuspended += delegate(object sender, WorkflowSuspendedEventArgs args)
            //{
            //    LoggingService.LogVerbose(
            //        "WorkflowFacade",
            //        string.Format("Workflow suspended  - Id = {0}, Error = {1}", args.WorkflowInstance.InstanceId, args.Error));
            //};

            _workflowRuntime.WorkflowTerminated += delegate(object sender, WorkflowTerminatedEventArgs args)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Format("Workflow terminated - Id = {0}, Exception:", args.WorkflowInstance.InstanceId));
                sb.AppendLine(args.Exception.Message);
                sb.AppendLine(args.Exception.StackTrace);

                Log.LogVerbose(LogTitle, sb.ToString());
            };

            _workflowRuntime.WorkflowUnloaded += delegate(object sender, WorkflowEventArgs args)
            {
                //LoggingService.LogVerbose(
                //    "WorkflowFacade",
                //    string.Format("Workflow unloaded   - Id = {0}", args.WorkflowInstance.InstanceId));
            };
        }



        private void LoadPersistedWorkflows()
        {
            foreach (Guid instanceId in _fileWorkFlowPersistenceService.GetPersistedWorkflows())
            {
                if ((_resourceLocker.Resources.WorkflowStatusDictionary.ContainsKey(instanceId) == false) ||
                    (_resourceLocker.Resources.WorkflowStatusDictionary[instanceId] != WorkflowInstanceStatus.Running))
                {
                    // This will make the runtime load the persised workflow
                    WorkflowInstance workflowInstance = null;
                    try
                    {
                        workflowInstance = WorkflowRuntime.GetWorkflow(instanceId);
                    }
                    catch (InvalidOperationException)
                    {
                        _fileWorkFlowPersistenceService.RemovePersistedWorkflow(instanceId);
                    }

                    if (workflowInstance != null
                        && !_resourceLocker.Resources.WorkflowPersistingTypeDictionary.ContainsKey(instanceId))
                    {
                        Type workflowType = workflowInstance.GetWorkflowDefinition().GetType();
                        SetWorkflowPersistingType(workflowType, instanceId);
                    }
                }
            }
        }



        private void LoadPerssistedFormDatas()
        {
            using (_resourceLocker.Locker)
            {
                foreach (string filename in C1Directory.GetFiles(SerializedWorkflowsDirectory, "*.xml"))
                {
                    string guidString = Path.GetFileNameWithoutExtension(filename);
                    Guid id = Guid.Empty;

                    try
                    {
                        id = new Guid(guidString);
                        XDocument doc = XDocumentUtils.Load(filename);
                        FormData formData = FormData.Deserialize(doc.Root);

                        if (_resourceLocker.Resources.FormData.ContainsKey(id) == false)
                        {
                            _resourceLocker.Resources.FormData.Add(id, formData);
                            FormsWorkflowBindingCache.Bindings.Add(id, formData.Bindings);
                        }
                    }
                    catch (DataSerilizationException)
                    {
                        //Log.LogVerbose(LogTitle, string.Format("The workflow {0} contained one or more bindings where data was deleted or data type changed, workflow can not be resumed", id));

                        //AbortWorkflow(id);
                    }
                    catch (Exception)
                    {
                        if (id != Guid.Empty)
                        {
                            Log.LogCritical("WorkflowFacade", "Could not deserialize form data for the workflow {0}", id);
                            AbortWorkflow(id);
                        }
                    }
                }
            }
        }


        private void RemoveNotPersistableWorkflowsState()
        {
            using (_resourceLocker.Locker)
            {
                IEnumerable<Guid> instanceIds =
                    from kvp in _resourceLocker.Resources.WorkflowPersistingTypeDictionary
                    where kvp.Value == WorkflowPersistingType.Never
                    select kvp.Key;

                foreach (Guid instanceId in instanceIds)
                {
                    _fileWorkFlowPersistenceService.RemovePersistedWorkflow(instanceId);
                }
            }
        }



        private void UnloadWorkflowsSilent()
        {
            _fileWorkFlowPersistenceService.PersistAll = true;

            HashSet<Guid> abortedWorkflows = new HashSet<Guid>(_fileWorkFlowPersistenceService.GetAbortedWorkflows());

            foreach (Guid instanceId in _resourceLocker.Resources.WorkflowStatusDictionary.Keys.ToList())
            {
                if (abortedWorkflows.Contains(instanceId))
                {
                    continue;
                }

                /*WorkflowPersistingType workflowPersistingType;

                if (_resourceLocker.Resources.WorkflowPersistingTypeDictionary.TryGetValue(instanceId, out workflowPersistingType)
                    && workflowPersistingType != WorkflowPersistingType.Never)
                {
                    UnloadSilent(instanceId);
                }*/

                UnloadSilent(instanceId);
            }

            _fileWorkFlowPersistenceService.PersistAll = false;
        }



        [DebuggerHidden]
        private void UnloadSilent(Guid instanceId)
        {
            try
            {
                WorkflowInstance workflowInstance = WorkflowRuntime.GetWorkflow(instanceId);
                workflowInstance.Unload();
            }
            catch (Exception)
            {
                // Ignore, the workflow is already dead
            }
        }


        static List<Guid> AbortedWorkflows = new List<Guid>();

        private void PersistFormData(Guid instanceId)
        {
            var resources = _resourceLocker.Resources;

            bool shouldPersist =
                resources.WorkflowPersistingTypeDictionary
                         .Any(f => f.Key == instanceId && f.Value != WorkflowPersistingType.Never);

            if (!shouldPersist) return;


            FormData formData = resources.FormData.
                Where(f => f.Key == instanceId).
                Select(f => f.Value).
                SingleOrDefault();

            if (formData == null) return;

            try
            {
                XElement element = formData.Serialize();

                string filename = Path.Combine(SerializedWorkflowsDirectory, string.Format("{0}.xml", instanceId));

                XDocument doc = new XDocument(element);
                doc.SaveToFile(filename);

                Log.LogVerbose(LogTitle, string.Format("FormData persisted for workflow id = {0}", instanceId));
            }
            catch (Exception ex)
            {
                // Stop trying serializing this workflow
                AbortWorkflow(instanceId);

                Log.LogCritical(LogTitle, ex);
            }
        }



        private void PersistFormData()
        {
            var resources = _resourceLocker.Resources;

            List<Guid> instanceIds =
                (from kvp in resources.WorkflowPersistingTypeDictionary
                 where kvp.Value != WorkflowPersistingType.Never
                 select kvp.Key).ToList();

            // Copying collection since it may be modified why execution of forech-statement
            var formDataSetToBePersisted = resources.FormData.Where(f => instanceIds.Contains(f.Key)).ToList();

            foreach (var kvp in formDataSetToBePersisted)
            {
                Guid instanceid = kvp.Key;

                try
                {
                    XElement element = kvp.Value.Serialize();

                    string filename = Path.Combine(SerializedWorkflowsDirectory, string.Format("{0}.xml", instanceid));

                    XDocument doc = new XDocument(element);
                    doc.SaveToFile(filename);

                    Log.LogVerbose(LogTitle, "FormData persisted for workflow id = " + instanceid);
                }
                catch (Exception ex)
                {
                    // Stop trying serializing this workflow
                    AbortWorkflow(instanceid);

                    Log.LogCritical(LogTitle, ex);
                }
            }
        }



        private void DeletePersistedWorkflow(Guid instanceId)
        {
            using (GlobalInitializerFacade.CoreIsInitializedScope)
            {
                _fileWorkFlowPersistenceService.RemovePersistedWorkflow(instanceId);
            }
        }



        private void DeletePersistedFormData(Guid instanceId)
        {
            using (GlobalInitializerFacade.CoreIsInitializedScope)
            {
                string filename = Path.Combine(SerializedWorkflowsDirectory, string.Format("{0}.xml", instanceId));

                if (C1File.Exists(filename))
                {
                    C1File.Delete(filename);

                    Log.LogVerbose(LogTitle, string.Format("Persisted FormData deleted for workflow id = {0}", instanceId));
                }
            }
        }



        private void DeleteOldWorkflows()
        {
            using (GlobalInitializerFacade.CoreIsInitializedScope)
            {
                foreach (string filename in C1Directory.GetFiles(SerializedWorkflowsDirectory))
                {
                    DateTime creationTime = C1File.GetLastWriteTime(filename);

                    if (DateTime.Now.Subtract(creationTime) > _oldFileExistensTimeout)
                    {
                        Guid instanceId = new Guid(Path.GetFileNameWithoutExtension(filename));

                        if (Path.GetExtension(filename) == "bin")
                        {
                            try
                            {
                                WorkflowRuntime.GetWorkflow(instanceId);
                                AbortWorkflow(instanceId);
                            }
                            catch (Exception)
                            {
                            }
                        }

                        C1File.Delete(filename);

                        Log.LogVerbose(LogTitle, string.Format("Old workflow instance file deleted {0}", filename));
                    }
                }
            }
        }



        private static string SerializedWorkflowsDirectory
        {
            get
            {
                string directory = PathUtil.Resolve(GlobalSettingsFacade.SerializedWorkflowsDirectory);
                if (C1Directory.Exists(directory) == false)
                {
                    C1Directory.CreateDirectory(directory);
                }

                return directory;
            }
        }


        private static bool ConsoleIdEquals(FlowControllerServicesContainer flowControllerServicesContainer, string consoleId)
        {
            if (flowControllerServicesContainer == null) return false;

            IManagementConsoleMessageService managementConsoleMessageService = flowControllerServicesContainer.GetService<IManagementConsoleMessageService>();

            if (managementConsoleMessageService == null) return false;

            if (managementConsoleMessageService.CurrentConsoleId == consoleId) return true;

            return false;
        }



        private enum WorkflowInstanceStatus
        {
            Idle,
            Running,
            Terminated
        }



        private sealed class Resources
        {
            public Resources()
            {
                this.WorkflowStatusDictionary = new Dictionary<Guid, WorkflowInstanceStatus>();
                this.FormData = new Dictionary<Guid, FormData>();
                this.FlowControllerServicesContainers = new Dictionary<Guid, FlowControllerServicesContainer>();
                this.WorkflowPersistingTypeDictionary = new Dictionary<Guid, WorkflowPersistingType>();
                this.EventHandleFilters = new Dictionary<Type, IEventHandleFilter>();
            }

            public Dictionary<Guid, WorkflowInstanceStatus> WorkflowStatusDictionary { get; set; }
            public Dictionary<Guid, FormData> FormData { get; set; }
            public Dictionary<Guid, FlowControllerServicesContainer> FlowControllerServicesContainers { get; set; }

            public Dictionary<Guid, WorkflowPersistingType> WorkflowPersistingTypeDictionary { get; set; }

            public Dictionary<Guid, Semaphore> WorkflowIdleWaitSemaphoes { get; set; }
            public Dictionary<int, Exception> ExceptionFromWorkflow { get; set; }

            public Dictionary<Type, IEventHandleFilter> EventHandleFilters { get; set; }

            public static void InitializeResources(Resources resources)
            {
                IEnumerable<Guid> instanceIds =
                    (from kvp in resources.WorkflowPersistingTypeDictionary
                     where kvp.Value == WorkflowPersistingType.Never
                     select kvp.Key).ToList();

                foreach (Guid instanceId in instanceIds)
                {
                    if (resources.WorkflowStatusDictionary.ContainsKey(instanceId))
                    {
                        resources.WorkflowStatusDictionary.Remove(instanceId);
                    }

                    if (resources.FormData.ContainsKey(instanceId))
                    {
                        resources.FormData.Remove(instanceId);
                    }

                    if (resources.FlowControllerServicesContainers.ContainsKey(instanceId))
                    {
                        resources.FlowControllerServicesContainers.Remove(instanceId);
                    }

                    resources.WorkflowPersistingTypeDictionary.Remove(instanceId);
                }

                resources.WorkflowIdleWaitSemaphoes = new Dictionary<Guid, Semaphore>();
                resources.ExceptionFromWorkflow = new Dictionary<int, Exception>();
            }
        }
    }
}
