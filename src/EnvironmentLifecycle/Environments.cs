﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DotNetTestkit.EnvironmentLifecycle
{

    public class Environments
    {
        private static IDictionary<Type, IEnvironmentLifecycle> environments = new Dictionary<Type, IEnvironmentLifecycle>();
        private static IDictionary<Type, Task<EnvironmentStartResult>> environmentStartTasks = new Dictionary<Type, Task<EnvironmentStartResult>>();

        static Environments()
        {
            AppDomain.CurrentDomain.DomainUnload += StopEnvironments;
        }

        public static IEnvironmentLifecycle ForType(Type type)
        {
            var environmentTypes = type.GetCustomAttributes(typeof(SetUpEnvironmentAttribute), true)
                .Select(attr => (SetUpEnvironmentAttribute)attr)
                .SelectMany(attr => attr.Environments);

            return new AggregatedEnvironments(environmentTypes);
        }

        public static T GetStartedEnvironment<T>() where T : IEnvironmentLifecycle, new()
        {
            var environment = GetEnvironment(typeof(T));

            EnsureEnvironmentStarted(environment);

            return (T) environment;
        }

        public static T GetEnvironment<T>() where T : IEnvironmentLifecycle, new()
        {
            return (T) GetEnvironment(typeof(T));
        }

        public static IEnvironmentLifecycle GetEnvironment(Type envType)
        {
            IEnvironmentLifecycle environment;

            lock (environments)
            {
                if (!environments.TryGetValue(envType, out environment))
                {
                    lock (environments)
                    {
                        environment = (IEnvironmentLifecycle) Activator.CreateInstance(envType);

                        environments.Add(envType, environment);
                    }
                }
            }

            return environment;
        }

        private static void EnsureEnvironmentStarted(IEnvironmentLifecycle environment)
        {
            Task<EnvironmentStartResult> environmentStarting;

            lock (environmentStartTasks)
            {
                if (!environmentStartTasks.TryGetValue(environment.GetType(), out environmentStarting))
                {
                    lock (environmentStartTasks)
                    {
                        environmentStarting = new Task<EnvironmentStartResult>(() =>
                        {
                            try
                            {
                                environment.Start();
                                return EnvironmentStartResult.Success;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Failed starting {0}", environment.GetType().FullName);
                                Console.WriteLine(ex.ToString());
                                return EnvironmentStartResult.Failed;
                            }
                        });

                        environmentStarting.Start();

                        environmentStartTasks.Add(environment.GetType(), environmentStarting);
                    }
                }
            }

            environmentStarting.Wait();
        }

        private static void StopEnvironments(object sender, EventArgs e)
        {
            foreach (var env in environments.Values.ToList())
            {
                try
                {
                    env.Stop();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed stopping {0}", env.GetType().FullName);
                    Console.WriteLine(ex.ToString());
                }
            }
        }
    }

    public class EnvironmentStarter: MarshalByRefObject
    {
        public IEnvironmentLifecycle ForType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Console.WriteLine(assembly.FullName);

                var type = assembly.GetType(typeName);

                if (type != null)
                {
                    return new RemoteEnvironmentLifecycle(Environments.ForType(type));
                }
            }

            return null;
        }

        public void Setup(string pathToAssembly, TextWriter output, TextWriter error)
        {
            Console.SetOut(output);
            Console.SetError(error);

            var assemblyName = Path.GetFileNameWithoutExtension(pathToAssembly);

            Assembly.Load(assemblyName);
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }
    }

    public class RemoteEnvironmentLifecycle : MarshalByRefObject, IEnvironmentLifecycle
    {
        private readonly IEnvironmentLifecycle environment;

        public RemoteEnvironmentLifecycle(IEnvironmentLifecycle environment)
        {
            this.environment = environment;
        }

        public void Reload()
        {
            environment.Reload();
        }

        public void Start()
        {
            environment.Start();
        }

        public void Stop()
        {
            environment.Stop();
        }
    }

    internal class AggregatedEnvironments : IEnvironmentLifecycle
    {
        private readonly List<IEnvironmentLifecycle> environments;

        public AggregatedEnvironments(IEnumerable<Type> environmentTypes)
        {
            environments = environmentTypes.Select(envType => Environments.GetEnvironment(envType)).ToList();
        }

        public void Reload()
        {
            ForEachEnv(env => env.Reload());
        }

        public void Start()
        {
            ForEachEnv(env => env.Start());
        }

        public void Stop()
        {
            ForEachEnv(env => env.Stop());
        }

        private void ForEachEnv(Action<IEnvironmentLifecycle> fun)
        {
            foreach (var env in environments) {
                try
                {
                    fun(env);
                } catch
                {

                }
            }
        }
    }

    public interface IEnvironmentLifecycle
    {
        void Start();
        void Reload();
        void Stop();
    }

    public enum EnvironmentStartResult
    {
        Success,
        Failed
    }

    public class SetUpEnvironmentAttribute : Attribute
    {
        private readonly Type[] environments;

        public SetUpEnvironmentAttribute(params Type[] environments)
        {
            this.environments = environments;
        }

        public Type[] Environments
        {
            get
            {
                return environments;
            }
        }
    }
}