// OpenGL renderer: HDR scene -> bloom pyramid -> tonemapped composite, per window, one shared context.
using System;
using System.Collections.Generic;

namespace ScreenWorm
{
    class GlProgram
    {
        public readonly uint Id; readonly Dictionary<string, int> locs = new Dictionary<string, int>();
        public GlProgram(string vs, string fs, string label, string[] varyings) { Id = GL.Link(vs, fs, label, varyings); }
        public void Use() { GL.glUseProgram(Id); }
        public int U(string name) { int l; if (!locs.TryGetValue(name, out l)) { l = GL.glGetUniformLocation(Id, name); locs[name] = l; } return l; }
        public void F(string n, double v) { GL.glUniform1f(U(n), (float)v); }
        public void F2(string n, double a, double b) { GL.glUniform2f(U(n), (float)a, (float)b); }
        public void F3(string n, double a, double b, double c) { GL.glUniform3f(U(n), (float)a, (float)b, (float)c); }
        public void F4(string n, double a, double b, double c, double d) { GL.glUniform4f(U(n), (float)a, (float)b, (float)c, (float)d); }
        public void I(string n, int v) { GL.glUniform1i(U(n), v); }
        public void UI(string n, uint v) { GL.glUniform1ui(U(n), v); }
        public void View(RectD v) { F4("uView", v.X, v.Y, v.W, v.H); }
    }

    class Target
    {
        public uint Fbo, Tex; public int W, H;
        public static Target Create(int w, int h, uint internalFormat, uint type)
        {
            var t = new Target { W = w, H = h };
            GL.glGenTextures(1, out t.Tex); GL.glBindTexture(GL.TEXTURE_2D, t.Tex);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR); GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE); GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
            GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)internalFormat, w, h, 0, GL.RGBA, type, IntPtr.Zero);
            GL.glGenFramebuffers(1, out t.Fbo); GL.glBindFramebuffer(GL.FRAMEBUFFER, t.Fbo);
            GL.glFramebufferTexture2D(GL.FRAMEBUFFER, GL.COLOR_ATTACHMENT0, GL.TEXTURE_2D, t.Tex, 0);
            uint st = GL.glCheckFramebufferStatus(GL.FRAMEBUFFER); GL.glBindFramebuffer(GL.FRAMEBUFFER, 0);
            if (st != GL.FRAMEBUFFER_COMPLETE) throw new Exception("framebuffer incomplete 0x" + st.ToString("x") + " (" + w + "x" + h + ")");
            return t;
        }
        public void Dispose() { if (Fbo != 0) GL.glDeleteFramebuffers(1, ref Fbo); if (Tex != 0) GL.glDeleteTextures(1, ref Tex); Fbo = 0; Tex = 0; }
    }

    // Per-window render targets.
    class ViewRes
    {
        public Target Hdr, Final; public Target[] Down = new Target[0], Up = new Target[0]; public int W, H; public double Quality;
        public void Build(int w, int h, double quality, bool offscreenFinal)
        {
            Dispose(); W = w; H = h; Quality = quality;
            int rw = Math.Max(8, (int)Math.Round(w * quality)), rh = Math.Max(8, (int)Math.Round(h * quality));
            Hdr = Target.Create(rw, rh, GL.RGBA16F, GL.HALF_FLOAT);
            var down = new List<Target>(); var up = new List<Target>(); int mw = rw >> 1, mh = rh >> 1;
            while (down.Count < 6 && Math.Min(mw, mh) >= 8) { down.Add(Target.Create(mw, mh, GL.RGBA16F, GL.HALF_FLOAT)); up.Add(Target.Create(mw, mh, GL.RGBA16F, GL.HALF_FLOAT)); mw >>= 1; mh >>= 1; }
            Down = down.ToArray(); Up = up.ToArray();
            if (offscreenFinal) Final = Target.Create(w, h, GL.RGBA8, GL.UNSIGNED_BYTE);
        }
        public void Dispose()
        {
            if (Hdr != null) Hdr.Dispose(); Hdr = null;
            foreach (var t in Down) t.Dispose(); foreach (var t in Up) t.Dispose(); Down = new Target[0]; Up = new Target[0];
            if (Final != null) Final.Dispose(); Final = null;
        }
    }

    class DynBuf
    {
        public uint Vbo; int cap;
        public DynBuf() { GL.glGenBuffers(1, out Vbo); }
        public void Upload(float[] data, int count)
        {
            GL.glBindBuffer(GL.ARRAY_BUFFER, Vbo);
            if (count > cap) { cap = Math.Max(count, Math.Max(cap * 2, 4096)); GL.glBufferData(GL.ARRAY_BUFFER, (IntPtr)((long)cap * 4), IntPtr.Zero, GL.STREAM_DRAW); }
            if (count > 0) GL.glBufferSubDataF(GL.ARRAY_BUFFER, IntPtr.Zero, (IntPtr)((long)count * 4), data);
        }
    }

    class Settings { public double Bloom = 1, Crt = 0, Aberration = 1, Exposure = 1.0, Grain = 0.028; }

    class Renderer
    {
        public const int HeadTexW = 256; const int TrailMaxPoints = Sim.TrailCap + 8;
        GlProgram pBg, pStar, pWorm, pHead, pPartU, pPart, pComet, pRing, pDown, pUp, pComp, pHud;
        uint quadVao, quadVbo;
        readonly uint[] pA = new uint[2], pB = new uint[2], pVao = new uint[2], pTf = new uint[2]; int pCur; public uint PStep;
        public readonly int ParticleCap; public int ParticleCount, MoteCount;
        uint headTex; readonly float[] headData = new float[HeadTexW * 2 * 4];
        uint starVao, starVbo; int starCount;
        readonly DynBuf wormBuf, headBuf, cometBuf, ringBuf, hudBuf; readonly uint wormVao, headVao, cometVao, ringVao, hudVao;
        readonly float[] wormData; readonly float[] scratch = new float[1 << 16]; readonly float[] hudQuad = new float[24];
        RectD world; public string Info = ""; float maxPoint = 64;

        public Renderer(int particleCap, int moteCount, int cores)
        {
            ParticleCap = Math.Max(4096, particleCap); ParticleCount = ParticleCap; MoteCount = Math.Max(0, Math.Min(moteCount, ParticleCap >> 3));
            Info = GL.glGetString(GL.RENDERER) + " | OpenGL " + GL.glGetString(GL.VERSION);
            var range = new float[2]; GL.glGetFloatv(GL.POINT_SIZE_RANGE, range); if (range[1] > 0) maxPoint = range[1];
            GL.glEnable(GL.PROGRAM_POINT_SIZE); GL.glGetError();
            pBg = new GlProgram(Shaders.QuadVs, Shaders.BgFs, "bg", null);
            pStar = new GlProgram(Shaders.StarVs, Shaders.StarFs, "stars", null);
            pWorm = new GlProgram(Shaders.WormVs, Shaders.WormFs, "worm", null);
            pHead = new GlProgram(Shaders.HeadVs, Shaders.HeadFs, "head", null);
            pPartU = new GlProgram(Shaders.PartUpdateVs, Shaders.PartUpdateFs, "particles-update", new[] { "vA", "vB" });
            pPart = new GlProgram(Shaders.PartVs, Shaders.PartFs, "particles", null);
            pComet = new GlProgram(Shaders.CometVs, Shaders.CometFs, "comet", null);
            pRing = new GlProgram(Shaders.RingVs, Shaders.RingFs, "ring", null);
            pDown = new GlProgram(Shaders.QuadVs, Shaders.BloomDownFs, "bloom-down", null);
            pUp = new GlProgram(Shaders.QuadVs, Shaders.BloomUpFs, "bloom-up", null);
            pComp = new GlProgram(Shaders.QuadVs, Shaders.CompositeFs, "composite", null);
            pHud = new GlProgram(Shaders.HudVs, Shaders.HudFs, "hud", null);

            GL.glGenVertexArrays(1, out quadVao); GL.glBindVertexArray(quadVao);
            GL.glGenBuffers(1, out quadVbo); GL.glBindBuffer(GL.ARRAY_BUFFER, quadVbo);
            var q = new float[] { -1, -1, 3, -1, -1, 3 };
            GL.glBufferDataF(GL.ARRAY_BUFFER, (IntPtr)(q.Length * 4), q, GL.STATIC_DRAW);
            GL.glEnableVertexAttribArray(0); GL.glVertexAttribPointer(0, 2, GL.FLOAT, 0, 0, IntPtr.Zero);
            GL.glBindVertexArray(0);

            BuildParticles();
            wormBuf = new DynBuf(); wormVao = MakeVao(wormBuf.Vbo, new[] { 2, 2, 2 }, 24);
            headBuf = new DynBuf(); headVao = MakeVao(headBuf.Vbo, new[] { 2, 4, 2 }, 32);
            cometBuf = new DynBuf(); cometVao = MakeVao(cometBuf.Vbo, new[] { 2, 2, 4 }, 32);
            ringBuf = new DynBuf(); ringVao = MakeVao(ringBuf.Vbo, new[] { 2, 2, 4, 2 }, 40);
            hudBuf = new DynBuf(); hudVao = MakeVao(hudBuf.Vbo, new[] { 2, 2 }, 16);
            wormData = new float[Math.Max(1, cores) * TrailMaxPoints * 12];

            GL.glGenTextures(1, out headTex); GL.glBindTexture(GL.TEXTURE_2D, headTex);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.NEAREST); GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.NEAREST);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE); GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
            GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA32F, HeadTexW, 2, 0, GL.RGBA, GL.FLOAT, IntPtr.Zero);
            GL.Check("renderer init");
        }

        static uint MakeVao(uint vbo, int[] sizes, int stride)
        {
            uint vao; GL.glGenVertexArrays(1, out vao); GL.glBindVertexArray(vao); GL.glBindBuffer(GL.ARRAY_BUFFER, vbo);
            int off = 0;
            for (uint i = 0; i < sizes.Length; i++) { GL.glEnableVertexAttribArray(i); GL.glVertexAttribPointer(i, sizes[i], GL.FLOAT, 0, stride, (IntPtr)off); off += sizes[i] * 4; }
            GL.glBindVertexArray(0);
            return vao;
        }

        void BuildParticles()
        {
            int n = ParticleCap;
            var initA = new float[n * 4]; var initB = new float[n * 4];
            for (int i = 0; i < n; i++) { initB[i * 4] = -1; initB[i * 4 + 1] = 0; }
            for (int k = 0; k < 2; k++)
            {
                GL.glGenBuffers(1, out pA[k]); GL.glBindBuffer(GL.ARRAY_BUFFER, pA[k]); GL.glBufferDataF(GL.ARRAY_BUFFER, (IntPtr)((long)initA.Length * 4), initA, GL.DYNAMIC_COPY);
                GL.glGenBuffers(1, out pB[k]); GL.glBindBuffer(GL.ARRAY_BUFFER, pB[k]); GL.glBufferDataF(GL.ARRAY_BUFFER, (IntPtr)((long)initB.Length * 4), initB, GL.DYNAMIC_COPY);
            }
            for (int k = 0; k < 2; k++)
            {
                GL.glGenVertexArrays(1, out pVao[k]); GL.glBindVertexArray(pVao[k]);
                GL.glBindBuffer(GL.ARRAY_BUFFER, pA[k]); GL.glEnableVertexAttribArray(0); GL.glVertexAttribPointer(0, 4, GL.FLOAT, 0, 16, IntPtr.Zero);
                GL.glBindBuffer(GL.ARRAY_BUFFER, pB[k]); GL.glEnableVertexAttribArray(1); GL.glVertexAttribPointer(1, 4, GL.FLOAT, 0, 16, IntPtr.Zero);
                GL.glBindVertexArray(0);
                GL.glGenTransformFeedbacks(1, out pTf[k]); GL.glBindTransformFeedback(GL.TRANSFORM_FEEDBACK, pTf[k]);
                GL.glBindBufferBase(GL.TRANSFORM_FEEDBACK_BUFFER, 0, pA[1 - k]); GL.glBindBufferBase(GL.TRANSFORM_FEEDBACK_BUFFER, 1, pB[1 - k]);
                GL.glBindTransformFeedback(GL.TRANSFORM_FEEDBACK, 0);
            }
        }

        public void SetQuality(double quality)
        {
            ParticleCount = Math.Max(MoteCount, ((int)Math.Round(ParticleCap * Math.Max(0.2, quality))) & ~63);
        }

        public void SetWorld(RectD w, uint seed)
        {
            world = w;
            var rnd = new Mulberry(seed ^ 0x51a7u);
            int n = (int)Math.Round(Math.Min(7000, Math.Max(400, w.W * w.H / 4500)));
            var data = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                data[i * 4] = (float)(w.X + rnd.Next() * w.W); data[i * 4 + 1] = (float)(w.Y + rnd.Next() * w.H);
                data[i * 4 + 2] = (float)Math.Pow(rnd.Next(), 2.5); data[i * 4 + 3] = (float)rnd.Next();
            }
            if (starVao != 0) { GL.glDeleteVertexArrays(1, ref starVao); GL.glDeleteBuffers(1, ref starVbo); }
            GL.glGenBuffers(1, out starVbo); GL.glBindBuffer(GL.ARRAY_BUFFER, starVbo); GL.glBufferDataF(GL.ARRAY_BUFFER, (IntPtr)(data.Length * 4), data, GL.STATIC_DRAW);
            starVao = MakeVao(starVbo, new[] { 2, 2 }, 16); starCount = n;
        }

        void UploadHeads(Sim sim)
        {
            int n = Math.Min(sim.Worms.Count, HeadTexW);
            for (int i = 0; i < n; i++)
            {
                var w = sim.Worms[i]; int o = i * 4, o2 = (HeadTexW + i) * 4;
                headData[o] = (float)w.X; headData[o + 1] = (float)w.Y; headData[o + 2] = (float)Math.Cos(w.Ang); headData[o + 3] = (float)Math.Sin(w.Ang);
                headData[o2] = (float)w.Util; headData[o2 + 1] = w.R; headData[o2 + 2] = w.G; headData[o2 + 3] = w.B;
            }
            GL.glActiveTexture(GL.TEXTURE0); GL.glBindTexture(GL.TEXTURE_2D, headTex);
            GL.glTexSubImage2Df(GL.TEXTURE_2D, 0, 0, 0, HeadTexW, 2, GL.RGBA, GL.FLOAT, headData);
        }

        // One 60 Hz particle step on the GPU through transform feedback.
        public void StepParticles(Sim sim, float dt)
        {
            UploadHeads(sim);
            pPartU.Use();
            pPartU.F("uDt", dt); pPartU.F("uTime", PStep / 60.0); pPartU.UI("uStep", PStep);
            pPartU.F("uSpawn", 0.55 + 0.45 * (ParticleCount / (double)ParticleCap));
            pPartU.I("uWormCount", Math.Min(sim.Worms.Count, HeadTexW)); pPartU.I("uMoteCount", MoteCount);
            pPartU.F4("uWorld", world.X, world.Y, world.W, world.H); pPartU.I("uHeads", 0);
            GL.glBindVertexArray(pVao[pCur]);
            GL.glBindTransformFeedback(GL.TRANSFORM_FEEDBACK, pTf[pCur]);
            GL.glEnable(GL.RASTERIZER_DISCARD);
            GL.glBeginTransformFeedback(GL.POINTS);
            GL.glDrawArrays(GL.POINTS, 0, ParticleCount);
            GL.glEndTransformFeedback();
            GL.glDisable(GL.RASTERIZER_DISCARD);
            GL.glBindTransformFeedback(GL.TRANSFORM_FEEDBACK, 0);
            GL.glBindVertexArray(0);
            pCur = 1 - pCur; PStep++;
        }

        static double Clamp(double v, double a, double b) { return v < a ? a : v > b ? b : v; }

        public void Render(Sim sim, ViewRes vr, RectD view, Settings s, HudTexture hud)
        {
            var st = sim.Stats; double time = sim.Time;
            double pointScale = vr.Quality * Clamp(Math.Min(vr.W, vr.H) / 1080.0, 0.6, 1.6);

            GL.glBindFramebuffer(GL.FRAMEBUFFER, vr.Hdr.Fbo);
            GL.glViewport(0, 0, vr.Hdr.W, vr.Hdr.H);
            GL.glDisable(GL.BLEND);
            pBg.Use(); pBg.View(view); pBg.F4("uWorld", world.X, world.Y, world.W, world.H);
            pBg.F("uTime", time); pBg.F("uMem", st.Mem); pBg.F("uCpu", st.Cpu);
            DrawQuad();

            GL.glEnable(GL.BLEND); GL.glBlendFunc(GL.ONE, GL.ONE);
            if (starVao != 0)
            {
                pStar.Use(); pStar.View(view); pStar.F("uTime", time); pStar.F("uDim", 1.0 - 0.6 * st.Mem); pStar.F("uScale", pointScale);
                GL.glBindVertexArray(starVao); GL.glDrawArrays(GL.POINTS, 0, starCount); GL.glBindVertexArray(0);
            }
            DrawRings(sim, view);
            pPart.Use(); pPart.View(view);
            GL.glActiveTexture(GL.TEXTURE0); GL.glBindTexture(GL.TEXTURE_2D, headTex); pPart.I("uHeads", 0);
            pPart.F("uTime", time); pPart.F("uScale", pointScale); pPart.I("uMoteCount", MoteCount);
            pPart.I("uMoteShown", (int)Math.Round(MoteCount * Clamp(st.Threads / 10000.0, 0.08, 1.0))); pPart.F("uMoteVis", 1.0);
            GL.glBindVertexArray(pVao[pCur]); GL.glDrawArrays(GL.POINTS, 0, ParticleCount); GL.glBindVertexArray(0);
            DrawWorms(sim, view, pointScale);
            DrawComets(sim, view);
            GL.glDisable(GL.BLEND);

            // Bloom pyramid: 13-tap downsample with a soft knee, tent-filter upsample.
            var down = vr.Down; var up = vr.Up;
            pDown.Use(); pDown.I("uTex", 0); pDown.F("uThreshold", 0.85);
            GL.glActiveTexture(GL.TEXTURE0);
            for (int i = 0; i < down.Length; i++)
            {
                var src = i == 0 ? vr.Hdr : down[i - 1]; var dst = down[i];
                GL.glBindFramebuffer(GL.FRAMEBUFFER, dst.Fbo); GL.glViewport(0, 0, dst.W, dst.H);
                GL.glBindTexture(GL.TEXTURE_2D, src.Tex);
                pDown.F2("uTexel", 1.0 / src.W, 1.0 / src.H); pDown.I("uFirst", i == 0 ? 1 : 0);
                DrawQuad();
            }
            Target bloom = down.Length > 0 ? down[down.Length - 1] : vr.Hdr;
            if (down.Length > 1)
            {
                pUp.Use(); pUp.I("uTex", 0); pUp.I("uPrev", 1); pUp.F("uRadius", 1.0);
                var prev = down[down.Length - 1];
                for (int i = down.Length - 2; i >= 0; i--)
                {
                    var dst = up[i];
                    GL.glBindFramebuffer(GL.FRAMEBUFFER, dst.Fbo); GL.glViewport(0, 0, dst.W, dst.H);
                    GL.glActiveTexture(GL.TEXTURE0); GL.glBindTexture(GL.TEXTURE_2D, prev.Tex);
                    GL.glActiveTexture(GL.TEXTURE1); GL.glBindTexture(GL.TEXTURE_2D, down[i].Tex);
                    pUp.F2("uTexel", 1.0 / prev.W, 1.0 / prev.H);
                    DrawQuad();
                    prev = dst;
                }
                bloom = prev;
            }

            GL.glBindFramebuffer(GL.FRAMEBUFFER, vr.Final != null ? vr.Final.Fbo : 0);
            GL.glViewport(0, 0, vr.W, vr.H);
            pComp.Use();
            GL.glActiveTexture(GL.TEXTURE0); GL.glBindTexture(GL.TEXTURE_2D, vr.Hdr.Tex); pComp.I("uScene", 0);
            GL.glActiveTexture(GL.TEXTURE1); GL.glBindTexture(GL.TEXTURE_2D, bloom.Tex); pComp.I("uBloom", 1);
            pComp.F("uBloomAmt", s.Bloom * (0.30 + 0.25 * st.Gpu)); pComp.F("uExposure", s.Exposure); pComp.F("uTime", time);
            pComp.F("uAberration", s.Aberration * (0.004 + 0.012 * st.Gpu)); pComp.F("uCrt", s.Crt); pComp.F("uGrain", s.Grain);
            DrawQuad();
            GL.glActiveTexture(GL.TEXTURE0);

            if (hud != null && hud.Visible)
            {
                GL.glEnable(GL.BLEND); GL.glBlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
                float x0 = -1, x1 = -1 + 2f * hud.W / vr.W, y0 = -1, y1 = -1 + 2f * hud.H / vr.H;
                float[] hq = hudQuad; int o = 0;
                hq[o++] = x0; hq[o++] = y1; hq[o++] = 0; hq[o++] = 0; hq[o++] = x1; hq[o++] = y1; hq[o++] = 1; hq[o++] = 0; hq[o++] = x0; hq[o++] = y0; hq[o++] = 0; hq[o++] = 1;
                hq[o++] = x1; hq[o++] = y1; hq[o++] = 1; hq[o++] = 0; hq[o++] = x1; hq[o++] = y0; hq[o++] = 1; hq[o++] = 1; hq[o++] = x0; hq[o++] = y0; hq[o++] = 0; hq[o++] = 1;
                hudBuf.Upload(hq, 24);
                pHud.Use(); GL.glBindTexture(GL.TEXTURE_2D, hud.Tex); pHud.I("uTex", 0); pHud.F("uAlpha", 1.0);
                GL.glBindVertexArray(hudVao); GL.glDrawArrays(GL.TRIANGLES, 0, 6); GL.glBindVertexArray(0);
                GL.glDisable(GL.BLEND);
            }
        }

        void DrawQuad() { GL.glBindVertexArray(quadVao); GL.glDrawArrays(GL.TRIANGLES, 0, 3); GL.glBindVertexArray(0); }

        void DrawWorms(Sim sim, RectD view, double pointScale)
        {
            int off = 0; var firsts = new int[sim.Worms.Count]; var counts = new int[sim.Worms.Count];
            for (int i = 0; i < sim.Worms.Count; i++)
            {
                if (off + TrailMaxPoints * 12 > wormData.Length) { counts[i] = 0; continue; }
                int n = sim.Body(sim.Worms[i], wormData, off);
                firsts[i] = off / 6; counts[i] = n * 2; off += n * 12;
            }
            wormBuf.Upload(wormData, off);
            pWorm.Use(); pWorm.View(view); pWorm.F("uTime", sim.Time);
            GL.glBindVertexArray(wormVao);
            for (int i = 0; i < sim.Worms.Count; i++)
            {
                if (counts[i] < 4) continue;
                var w = sim.Worms[i];
                pWorm.F("uLen", Math.Max(1, w.Len)); pWorm.F("uWidth", w.Width); pWorm.F("uUtil", w.Util); pWorm.F("uPhase", w.Phase);
                pWorm.F3("uColor", w.R, w.G, w.B); pWorm.F("uIntensity", 0.55 + 0.6 * w.Util + 0.2 * Math.Max(0, w.Perf - 1));
                GL.glDrawArrays(GL.TRIANGLE_STRIP, firsts[i], counts[i]);
            }
            GL.glBindVertexArray(0);
            int o = 0;
            foreach (var w in sim.Worms)
            {
                if (o + 8 > scratch.Length) break;
                scratch[o++] = (float)w.X; scratch[o++] = (float)w.Y; scratch[o++] = w.R; scratch[o++] = w.G; scratch[o++] = w.B; scratch[o++] = 1f;
                scratch[o++] = (float)Math.Min(maxPoint, (12 + 24 * w.Util + 1.2 * w.Width) * pointScale); scratch[o++] = (float)w.Util;
            }
            headBuf.Upload(scratch, o);
            pHead.Use(); pHead.View(view);
            GL.glBindVertexArray(headVao); GL.glDrawArrays(GL.POINTS, 0, o / 8); GL.glBindVertexArray(0);
        }

        void DrawComets(Sim sim, RectD view)
        {
            if (sim.Comets.Count == 0) return;
            int o = 0; var d = scratch;
            foreach (var c in sim.Comets)
            {
                if (o + 48 > d.Length) break;
                double sp = Math.Sqrt(c.Vx * c.Vx + c.Vy * c.Vy); if (sp < 1) sp = 1;
                double ax = c.Vx / sp, ay = c.Vy / sp, nx = -ay, ny = ax;
                double L = 120 + 280 * c.Size, hw = 4 + 7 * c.Size;
                float r = c.Kind == 0 ? 0.5f : 1.0f, g = c.Kind == 0 ? 0.85f : 0.7f, b = c.Kind == 0 ? 1.0f : 0.42f;
                float a = (float)((0.14 + 0.36 * c.Size) * Math.Min(1, c.Age * 4));
                double hx = c.X, hy = c.Y, tx = c.X - ax * L, ty = c.Y - ay * L;
                o = Vtx(d, o, hx + nx * hw * 1.4, hy + ny * hw * 1.4, 0, 0, r, g, b, a);
                o = Vtx(d, o, hx - nx * hw * 1.4, hy - ny * hw * 1.4, 0, 1, r, g, b, a);
                o = Vtx(d, o, tx + nx * hw, ty + ny * hw, 1, 0, r, g, b, a);
                o = Vtx(d, o, hx - nx * hw * 1.4, hy - ny * hw * 1.4, 0, 1, r, g, b, a);
                o = Vtx(d, o, tx - nx * hw, ty - ny * hw, 1, 1, r, g, b, a);
                o = Vtx(d, o, tx + nx * hw, ty + ny * hw, 1, 0, r, g, b, a);
            }
            cometBuf.Upload(d, o);
            pComet.Use(); pComet.View(view);
            GL.glBindVertexArray(cometVao); GL.glDrawArrays(GL.TRIANGLES, 0, o / 8); GL.glBindVertexArray(0);
        }
        static int Vtx(float[] d, int o, double x, double y, float u, float v, float r, float g, float b, float a)
        {
            d[o++] = (float)x; d[o++] = (float)y; d[o++] = u; d[o++] = v; d[o++] = r; d[o++] = g; d[o++] = b; d[o++] = a; return o;
        }

        static readonly int[] Corners = { -1, -1, 1, -1, -1, 1, 1, -1, 1, 1, -1, 1 };
        void DrawRings(Sim sim, RectD view)
        {
            if (sim.Rings.Count == 0) return;
            int o = 0; var d = scratch;
            foreach (var r in sim.Rings)
            {
                if (o + 60 > d.Length) break;
                double k = Math.Min(1, r.Age / r.Life);
                double radius = r.MaxR * (1 - Math.Pow(1 - k, 2.5)), th = 5 + 9 * r.Size, half = radius + th * 3;
                float cr = r.Kind == 0 ? 0.35f : 1.0f, cg = r.Kind == 0 ? 0.95f : 0.62f, cb = r.Kind == 0 ? 1.0f : 0.3f;
                float a = (float)(Math.Pow(1 - k, 1.6) * (0.3 + 0.7 * r.Size) * 0.55);
                for (int c = 0; c < 12; c += 2)
                {
                    int cx = Corners[c], cy = Corners[c + 1];
                    d[o++] = (float)(r.X + cx * half); d[o++] = (float)(r.Y + cy * half); d[o++] = cx; d[o++] = cy;
                    d[o++] = cr; d[o++] = cg; d[o++] = cb; d[o++] = a; d[o++] = (float)radius; d[o++] = (float)th;
                }
            }
            ringBuf.Upload(d, o);
            pRing.Use(); pRing.View(view);
            GL.glBindVertexArray(ringVao); GL.glDrawArrays(GL.TRIANGLES, 0, o / 10); GL.glBindVertexArray(0);
        }
    }
}
