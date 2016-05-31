﻿using System;
using System.Diagnostics;
using GoodAI.ToyWorld.Control;
using RenderingBase.RenderObjects.Buffers;
using RenderingBase.RenderObjects.Effects;
using RenderingBase.RenderObjects.Geometries;
using RenderingBase.RenderObjects.Textures;
using RenderingBase.RenderRequests;
using VRage.Collections;

namespace RenderingBase.Renderer
{
    public abstract class RendererBase<TWorld>
        : IDisposable
        where TWorld : class
    {
        #region Fields

        public uint SimTime { get; private set; }

        private readonly IterableQueue<IRenderRequestBaseInternal<TWorld>> m_renderRequestQueue = new IterableQueue<IRenderRequestBaseInternal<TWorld>>();

        public readonly GeometryManager GeometryManager = new GeometryManager();
        public readonly EffectManager EffectManager = new EffectManager();
        public readonly TextureManager TextureManager = new TextureManager();
        public readonly RenderTargetManager RenderTargetManager = new RenderTargetManager();

        #endregion

        #region Genesis

        internal RendererBase()
        {
            StaticVboFactory.Init();
        }

        public virtual void Dispose()
        {
            // Dispose of RRs
            foreach (IRenderRequestBaseInternal<TWorld> renderRequest in m_renderRequestQueue)
                renderRequest.Dispose();

            m_renderRequestQueue.Clear();

            StaticVboFactory.Clear();
        }

        #endregion

        #region Virtual stuff

        public abstract int Width { get; }
        public abstract int Height { get; }

        public abstract void CreateWindow(string title, int width, int height);
        public abstract void CreateContext();
        public abstract void MakeContextCurrent();
        public abstract void MakeContextNotCurrent();

        public virtual void Init()
        {
            m_renderRequestQueue.Clear();
        }

        public virtual void ProcessRequests(TWorld world)
        {
            SimTime++;
            MakeContextCurrent();

            foreach (IRenderRequestBaseInternal<TWorld> renderRequest in m_renderRequestQueue)
                renderRequest.OnPreDraw();

            foreach (IRenderRequestBaseInternal<TWorld> renderRequest in m_renderRequestQueue)
            {
                Process(renderRequest, world);
                CheckError();
            }

            foreach (IRenderRequestBaseInternal<TWorld> renderRequest in m_renderRequestQueue)
                renderRequest.OnPostDraw();
        }

        protected virtual void Process(IRenderRequestBaseInternal<TWorld> request, TWorld world)
        {
            request.Draw(this, world);
        }

        [Conditional("DEBUG")]
        public virtual void CheckError()
        { }

        #endregion


        public void EnqueueRequest(IRenderRequest request)
        {
            m_renderRequestQueue.Enqueue((IRenderRequestBaseInternal<TWorld>)request);
        }

        public void EnqueueRequest(IAvatarRenderRequest request)
        {
            m_renderRequestQueue.Enqueue((IRenderRequestBaseInternal<TWorld>)request);
        }
    }
}
