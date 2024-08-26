// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

// Microsoft XNA Community Game Platform
// Copyright (C) Microsoft Corporation. All rights reserved.

using System;

namespace Microsoft.Xna.Framework.Graphics
{
    /// <summary>
    /// The default effect used by SpriteBatch.
    /// </summary>
    public class SpriteEffect : Effect
    {
        private EffectParameter _matrixParam;
        private Viewport _lastViewport;
        private Matrix _projection;
        private ProjectionType _projectionType = ProjectionType.Orthographic;
        private float _fov = MathF.PI / 2.0f;
        private float _zFar = 1000f;
        private bool _dirty = true;

        /// <summary>
        ///     Type of projection used to render the sprites
        /// </summary>
        public ProjectionType ProjectionType
        {
            get => _projectionType;
            set
            {
                _dirty |= _projectionType != value;
                _projectionType = value;
            }
        }

        /// <summary>
        ///     If <see cref="ProjectionType"/> is <see cref="ProjectionType.Perspective"/>, the FOV used
        /// </summary>
        public float Fov
        {
            get => _fov;
            set
            {
                _dirty |= _fov != value;
                _fov = value;
            }
        }

        /// <summary>
        ///     The farthest Z value that will be rendered
        /// </summary>
        public float ZFar
        {
            get => _zFar;
            set
            {
                _dirty |= _zFar != value;
                _zFar = value;
            }
        }

        /// <summary>
        /// Creates a new SpriteEffect.
        /// </summary>
        public SpriteEffect(GraphicsDevice device)
            : base(device, EffectResource.SpriteEffect.Bytecode)
        {
            CacheEffectParameters();
        }

        /// <summary>
        /// An optional matrix used to transform the sprite geometry. Uses <see cref="Matrix.Identity"/> if null.
        /// </summary>
        public Matrix? TransformMatrix { get; set; }

        /// <summary>
        /// Creates a new SpriteEffect by cloning parameter settings from an existing instance.
        /// </summary>
        protected SpriteEffect(SpriteEffect cloneSource)
            : base(cloneSource)
        {
            CacheEffectParameters();
        }


        /// <summary>
        /// Creates a clone of the current SpriteEffect instance.
        /// </summary>
        public override Effect Clone()
        {
            return new SpriteEffect(this);
        }


        /// <summary>
        /// Looks up shortcut references to our effect parameters.
        /// </summary>
        void CacheEffectParameters()
        {
            _matrixParam = Parameters["MatrixTransform"];
        }

        /// <summary>
        /// Lazily computes derived parameter values immediately before applying the effect.
        /// </summary>
        protected internal override void OnApply()
        {
            var vp = GraphicsDevice.Viewport;
            if (_dirty || (vp.Width != _lastViewport.Width) || (vp.Height != _lastViewport.Height))
            {
                // Normal 3D cameras look into the -z direction (z = 1 is in front of z = 0). The
                // sprite batch layer depth is the opposite (z = 0 is in front of z = 1).
                // --> We get the correct matrix with near plane 0 and far plane -1.
                switch (_projectionType)
                {
                    case ProjectionType.Orthographic:
                        Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, -_zFar, out _projection);
                        break;
                    case ProjectionType.Perspective:
                        var zNear = vp.Width / MathF.Tan(_fov / 2) / 2;
                        var t = Matrix.CreateLookAt(new Vector3(vp.Width / 2f, vp.Height / 2f, -zNear),
                            new Vector3(vp.Width / 2f, vp.Height / 2f, 0), Vector3.Down);
                        Matrix.CreatePerspective(vp.Width, vp.Height, zNear, zNear + _zFar, out var b);
                        Matrix.Multiply(ref t, ref b, out _projection);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (GraphicsDevice.UseHalfPixelOffset)
                {
                    _projection.M41 += -0.5f * _projection.M11;
                    _projection.M42 += -0.5f * _projection.M22;
                }

                _lastViewport = vp;
                _dirty = false;
            }

            if (TransformMatrix.HasValue)
                _matrixParam.SetValue(TransformMatrix.GetValueOrDefault() * _projection);
            else
                _matrixParam.SetValue(_projection);
        }
    }
}
