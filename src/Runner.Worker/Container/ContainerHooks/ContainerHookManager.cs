﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitHub.DistributedTask.Pipelines.ContextData;
using GitHub.DistributedTask.WebApi;
using GitHub.Runner.Common;
using GitHub.Runner.Common.Util;
using GitHub.Runner.Sdk;
using GitHub.Runner.Worker.Handlers;
using GitHub.Services.WebApi;

namespace GitHub.Runner.Worker.Container
{
    [ServiceLocator(Default = typeof(ContainerHookManager))]
    public interface IContainerHookManager : IRunnerService
    {
        Task PrepareJobAsync(IExecutionContext context, List<ContainerInfo> containers);
        Task CleanupJobAsync(IExecutionContext context, List<ContainerInfo> containers);
        Task ContainerStepAsync(IExecutionContext context);
        Task RunScriptStepAsync(IExecutionContext context);
    }

    public class ContainerHookManager : RunnerService, IContainerHookManager
    {
        private const string ResponseFolderName = "hook_responses";
        private string HookIndexPath;
        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            HookIndexPath = $"{Environment.GetEnvironmentVariable(Constants.Hooks.ContainerHooksPath)}";
        }
        
        public async Task PrepareJobAsync(IExecutionContext context, List<ContainerInfo> containers)
        {
            Trace.Entering();
            var responsePath = GenerateResponsePath();
            var jobContainer = containers.Where(c => c.IsJobContainer).FirstOrDefault();
            var serviceContainers = containers.Where(c => c.IsJobContainer == false).ToList();

            var input = new ContainerHookInput
            {
                Command = ContainerHookCommand.PrepareJob,
                ResponseFile = responsePath,
                Args = new ContainerHookArgs
                {
                    JobContainer = jobContainer.GetHookContainer(),
                    Services = serviceContainers.Select(c => c.GetHookContainer()).ToList(),
                }
            };            

            var response = await ExecuteHookScript(context, input);
            context.JobContext.Container["id"] = new StringContextData(response.Context.Container.Id);
            jobContainer.ContainerId = response.Context.Container.Id;
            context.JobContext.Container["network"] = new StringContextData(response.Context.Container.Network);
            jobContainer.ContainerNetwork = response.Context.Container.Network;
            // var configEnvFormat = "--format \"{{range .Config.Env}}{{println .}}{{end}}\"";
            // var containerEnv = await _dockerManager.DockerInspect(executionContext, container.ContainerId, configEnvFormat);
            // container.ContainerRuntimePath = DockerUtil.ParsePathFromConfigEnv(containerEnv);

            for (var i = 0; i < response.Context.Services.Count; i++)
            {
                var container = response.Context.Services[i]; // TODO: Confirm that the order response.Context.Services is the same as serviceContainers
                var containerInfo = serviceContainers[i];
                containerInfo.ContainerId = container.Id;
                containerInfo.ContainerNetwork = container.Network;
                var service = new DictionaryContextData()
                {
                    ["id"] = new StringContextData(container.Id),
                    ["ports"] = new DictionaryContextData(),
                    ["network"] = new StringContextData(container.Network)
                };

                // TODO: workout port mappings + format
                // foreach (var portMapping in containerInfo.UserPortMappings)
                // {
                //     // TODO: currently the format is ports["80:8080"] = "80:8080", fix this?
                //     (service["ports"] as DictionaryContextData)[$"{portMapping.Key}:{portMapping.Value}"] = new StringContextData($"{portMapping.Key}:{portMapping.Value}");
                // }
                context.JobContext.Services[containerInfo.ContainerNetworkAlias] = service;
            }
        }

        public async Task CleanupJobAsync(IExecutionContext context, List<ContainerInfo> containers)
        {
            Trace.Entering();

            var responsePath = GenerateResponsePath();
            var input = new ContainerHookInput
            {
                Command = ContainerHookCommand.CleanupJob,
                ResponseFile = responsePath,
                Args = new ContainerHookArgs
                {
                    JobContainer = containers.Where(c => c.IsJobContainer).FirstOrDefault().GetHookContainer(),
                    Services = containers.Where(c => c.IsJobContainer == false).Select(c => c.GetHookContainer()).ToList(),
                    Network = containers.Where(c => !string.IsNullOrEmpty(c.ContainerNetwork)).FirstOrDefault()?.ContainerNetwork,
                }
            };
            var response = await ExecuteHookScript(context, input);
        }

        public async Task ContainerStepAsync(IExecutionContext context)
        {
            Trace.Entering();
            await Task.FromResult(0);
            throw new NotImplementedException();
        }

        public async Task RunScriptStepAsync(IExecutionContext context)
        {
            Trace.Entering();
            await Task.FromResult(0);
            throw new NotImplementedException();
        }

        private async Task<ContainerHookResponse> ExecuteHookScript(IExecutionContext context, ContainerHookInput input)
        {
            var scriptDirectory = Path.GetDirectoryName(HookIndexPath);
            var stepHost = HostContext.CreateService<IDefaultStepHost>();
            Dictionary<string, string> inputs = new()
            {
                ["standardInInput"] = JsonUtility.ToString(input),
                ["path"] = HookIndexPath,
                ["shell"] = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Externals), NodeUtil.GetInternalNodeVersion(), "bin", $"node{IOUtil.ExeExtension}") + " {0}" // TODO: fix hardcoded node path
            };

            var handlerFactory = HostContext.GetService<IHandlerFactory>();
            var handler = handlerFactory.Create(
                            context,
                            null,
                            stepHost,
                            new ScriptActionExecutionData(),
                            inputs,
                            environment: new Dictionary<string, string>(VarUtil.EnvironmentVariableKeyComparer),
                            context.Global.Variables,
                            actionDirectory: scriptDirectory,
                            localActionContainerSetupSteps: null) as ScriptHandler;
            handler.PrepareExecution(ActionRunStage.Pre); // TODO: find out stage, we only use Start in pre, but double check

            IOUtil.CreateEmptyFile(input.ResponseFile);
            await handler.RunAsync(ActionRunStage.Pre);
            if (handler.ExecutionContext.Result == TaskResult.Failed)
            {
                throw new Exception("Hook failed"); // TODO: better exception
            }
            
            var response = IOUtil.LoadObject<ContainerHookResponse>(input.ResponseFile);
            IOUtil.DeleteFile(input.ResponseFile);
            return response;
        }

        private string GenerateResponsePath()
        {
            return $"{Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Temp), ResponseFolderName)}/{Guid.NewGuid()}.json";
        }
    }
}