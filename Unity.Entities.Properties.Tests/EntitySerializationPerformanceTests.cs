﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Properties.Serialization;
using UnityEngine;
using Unity.Properties;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace Unity.Entities.Properties.Tests
{
    [TestFixture]
    //TODO: Convert to performance tests
    public sealed class EntitySerializationPerformanceTests
    {
        private World m_PreviousWorld;
        private World m_World;
        private EntityManager m_Manager;

        [SetUp]
        public void Setup()
        {
            m_PreviousWorld = World.Active;
            m_World = World.Active = new World("Test World");
            m_Manager = m_World.GetOrCreateManager<EntityManager>();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Manager != null)
            {
                m_World.Dispose();
                m_World = null;

                World.Active = m_PreviousWorld;
                m_PreviousWorld = null;
                m_Manager = null;
            }
        }

        /// <summary>
        /// Serializes 100,000 entities as json
        /// </summary>
        [Test]
        public void SerializationPerformance()
        {
            const int kCount = 100000;

            // Create kCount entities and assign some arbitrary component data
            for (var i = 0; i < kCount; ++i)
            {
                var entity = m_Manager.CreateEntity(typeof(TestComponent), typeof(TestComponent2), typeof(MathComponent), typeof(BlitComponent));

                var comp = m_Manager.GetComponentData<BlitComponent>(entity);
                comp.blit.x = 123f;
                comp.blit.y = 456.789;
                comp.blit.z = -12;
                comp.flt = 0.01f;

                m_Manager.SetComponentData(entity, comp);
            }

            // Create a reusable string buffer and JsonVisitor
            var buffer = new StringBuffer(4096);
            var visitor = new JsonVisitor { StringBuffer = buffer };

            using (var entities = m_Manager.GetAllEntities())
            {
                // Since we are testing raw serialization performance we pre warm the property type bag
                // This builds a property tree for each type
                // This is done on demand for newly discovered types
                // @NOTE This json string will also be used to debug the size for a single entity
                var container = new EntityContainer(m_Manager, entities[0]);

                var json = JsonSerializer.Serialize(ref container);

                var totalTimer = new Stopwatch();
                totalTimer.Start();

                foreach (var entity in entities)
                {
                    container = new EntityContainer(m_Manager, entity);

                    // Visit and write to the underlying StringBuffer, this is the raw json serialization
                    JsonSerializer.Serialize(ref container, visitor);

                    // @NOTE at this point we can call Write(buffer.Buffer, 0, buffer.Length)
                    buffer.Clear();
                }

                totalTimer.Stop();
            }
        }

        private struct SerializationJob : IJobParallelForBatch
        {
            [ReadOnly]
            public NativeArray<Entity> Entities;

            public void Execute(int startIndex, int count)
            {
                // @HACK need a reliable way having the entity manage for the given entities
                var manager = World.Active.GetExistingManager<EntityManager>();
                var buffer = new StringBuffer(4096);
                var visitor = new JsonVisitor { StringBuffer = buffer };

                var end = startIndex + count;
                for (var i = startIndex; i < end; i++)
                {
                    var container = new EntityContainer(manager, Entities[i]);
                    JsonSerializer.Serialize(ref container, visitor);

                    // @NOTE at this point we can call Write(buffer.Buffer, 0, buffer.Length)
                    buffer.Clear();
                }
            }
        }

        private struct WorkerThreadContext
        {
            public NativeArray<Entity> Entities;
            public int StartIndex;
            public int EndIndex;
            public string Output;
        }

        /// <summary>
        /// Serializes 100,000 entities as json using manual thread management
        ///
        /// This test exists as an example to quickly test stuff on the thread that is not supported by C# job system
        /// (e.g. disc I/O, managed objects, strings etc)
        /// </summary>
        [Test]
        public void SerializationPerformanceThreaded()
        {
            const int kCount = 100000;

            // Create kCount entities and assign some arbitrary component data
            for (var i = 0; i < kCount; ++i)
            {
                var entity = m_Manager.CreateEntity(typeof(TestComponent), typeof(TestComponent2), typeof(MathComponent), typeof(BlitComponent));

                var comp = m_Manager.GetComponentData<BlitComponent>(entity);
                comp.blit.x = 123f;
                comp.blit.y = 456.789;
                comp.blit.z = -12;
                comp.flt = 0.01f;

                m_Manager.SetComponentData(entity, comp);
            }

            using (var entities = m_Manager.GetAllEntities())
            {
                // Since we are testing raw serialization performance we rre warm the property type bag
                // This builds a property tree for each type
                // This is done on demand for newly discovered types
                // @NOTE This json string will also be used to debug the size for a single entity
                var container = new EntityContainer(m_Manager, entities[0]);

                var json = JsonSerializer.Serialize(ref container);

                var totalTimer = new Stopwatch();

                totalTimer.Start();

                var numThreads = Math.Max(1, Environment.ProcessorCount - 1);
                var threadCount = numThreads;
                var countPerThread = entities.Length / threadCount + 1;
                var threads = new Thread[threadCount];

                // Split the workload 'evenly' across numThreads (IJobParallelForBatch)
                for (int begin = 0, index = 0; begin < entities.Length; begin += countPerThread, index++)
                {
                    var context = new WorkerThreadContext
                    {
                        Entities = entities,
                        StartIndex = begin,
                        EndIndex = Mathf.Min(begin + countPerThread, entities.Length)
                    };

                    var thread = new Thread(obj =>
                    {
                        var buffer = new StringBuffer(4096);
                        var visitor = new JsonVisitor { StringBuffer = buffer };

                        var c = (WorkerThreadContext)obj;
                        for (int p = c.StartIndex, end = c.EndIndex; p < end; p++)
                        {
                            var entity = c.Entities[p];

                            container = new EntityContainer(m_Manager, entity);

                            JsonSerializer.Serialize(ref container, visitor);

                            // @NOTE at this point we can call Write(buffer.Buffer, 0, buffer.Length)
                            buffer.Clear();
                        }
                    })
                    { IsBackground = true };
                    thread.Start(context);
                    threads[index] = thread;
                }

                foreach (var thread in threads)
                {
                    thread.Join();
                }

                totalTimer.Stop();
            }
        }

        /// <summary>
        /// Serializes 100,000 entities as json using the C# job system
        /// </summary>
        [Test]
        public void SerializationPerformanceJob()
        {
            const int kCount = 100000;

            // Create kCount entities and assign some arbitrary component data
            for (var i = 0; i < kCount; ++i)
            {
                var entity = m_Manager.CreateEntity(typeof(TestComponent), typeof(TestComponent2), typeof(MathComponent), typeof(BlitComponent));

                var comp = m_Manager.GetComponentData<BlitComponent>(entity);
                comp.blit.x = 123f;
                comp.blit.y = 456.789;
                comp.blit.z = -12;
                comp.flt = 0.01f;

                m_Manager.SetComponentData(entity, comp);
            }

            using (var entities = m_Manager.GetAllEntities(Allocator.TempJob))
            {
                // Since we are testing raw serialization performance we pre warm the property type bag
                // This builds a property tree for each type
                // This is done on demand for newly discovered types
                // @NOTE This json string will also be used to debug the size for a single entity

                var container = new EntityContainer(m_Manager, entities[0]);

                var json = JsonSerializer.Serialize(ref container);

                var job = new SerializationJob
                {
                    Entities = entities
                };

                var totalTimer = new Stopwatch();
                totalTimer.Start();

                var handle = job.ScheduleBatch(entities.Length, 10000);
                handle.Complete();

                totalTimer.Stop();
            }
        }
    }
}
