﻿using GoodAI.Core;
using GoodAI.Modules.NeuralNetwork.Group;
using GoodAI.Modules.NeuralNetwork.Layers;
using GoodAI.Modules.RBM;
using GoodAI.Core.Task;
using GoodAI.Core.Utils;
using ManagedCuda.BasicTypes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YAXLib;

namespace GoodAI.Modules.NeuralNetwork.Tasks
{
    /// <author>GoodAI</author>
    /// <meta>mbr</meta>
    /// <status>Development</status>
    /// <summary>
    /// Tasks for Gaussian hidden layer.
    /// </summary>
    /// <description></description>
    [Description("FeedForward"), MyTaskInfo(OneShot = false)]
    public class MyGaussianForwardTask : MyAbstractForwardTask<MyGaussianHiddenLayer>
    {
        public MyGaussianForwardTask() { }

        private MyCudaKernel m_forwardSamplingKernel;
        private MyCudaKernel m_L1TermKernel;
        private MyCudaKernel m_L2TermKernel;
        private MyCudaKernel m_gaussianRegularizationKernel;

        public override void Init(int nGPU)
        {
            m_forwardSamplingKernel = MyKernelFactory.Instance.Kernel(nGPU, @"NeuralNetwork\Layer\FeedForwardKernels", "GaussianForwardSamplingKernel");
            m_L1TermKernel = MyKernelFactory.Instance.Kernel(nGPU, @"NeuralNetwork\Layer\RegularizationTermKernels", "L1TermKernel");
            m_L2TermKernel = MyKernelFactory.Instance.Kernel(nGPU, @"NeuralNetwork\Layer\RegularizationTermKernels", "L2TermKernel");
            m_gaussianRegularizationKernel = MyKernelFactory.Instance.Kernel(nGPU, @"NeuralNetwork\Layer\RegularizationTermKernels", "GaussianRegularizationKernel");
        }

        public override void Execute()
        {
            MyKernelFactory.Instance.GetRandDevice(Owner).GenerateUniform(Owner.RandomNormal.GetDevice(Owner));
            Owner.RandomNormal.CopyToMemoryBlock(Owner.Output, 0, 0, Owner.Output.Count);

            if (Owner.Generate.IsIncomingRised())
            {
                (Owner.Parent as MyNeuralNetworkGroup).SGD.TrainingRate = 0;

                for (int i = 0; i < Owner.Input.Count; i++)
                {
                    if (i < Owner.Input.Count / 2) Owner.Input.Host[i] = 0.0f;
                    else Owner.Input.Host[i] = 1.0f;
                }
                Owner.Input.SafeCopyToDevice();
            }

            m_forwardSamplingKernel.SetupExecution(Owner.Neurons);
            m_forwardSamplingKernel.Run(
                Owner.Input,
                Owner.Output,
                Owner.Bias,
                Owner.RandomNormal,
                Owner.Input.Count,
                Owner.Output.Count
            );

            if (Owner.ParentNetwork.L1 > 0) // don't take performance hit if L1 is not used
            {
                m_L1TermKernel.SetupExecution(m_L1TermKernel.MAX_THREADS);
                m_L1TermKernel.DynamicSharedMemory = m_L1TermKernel.BlockDimensions.x * sizeof(float);
                m_L1TermKernel.Run(
                    Owner.Weights,
                    Owner.L1Term,
                    Owner.Weights.Count
                );
            }

            if (Owner.ParentNetwork.L2 > 0) // don't take performance hit if L2 is not used
            {
                m_L2TermKernel.SetupExecution(m_L2TermKernel.MAX_THREADS);
                m_L2TermKernel.DynamicSharedMemory = m_L2TermKernel.BlockDimensions.x * sizeof(float);
                m_L2TermKernel.Run(
                    Owner.Weights,
                    Owner.L2Term,
                    Owner.Weights.Count
                );
            }

            m_gaussianRegularizationKernel.SetupExecution(m_gaussianRegularizationKernel.MAX_THREADS / 2);
            m_gaussianRegularizationKernel.DynamicSharedMemory = m_gaussianRegularizationKernel.BlockDimensions.x * sizeof(float);
            m_gaussianRegularizationKernel.Run(
                Owner.Input,
                Owner.Input.Count,
                Owner.Regularization
            );
        }
    }

    /// <author>GoodAI</author>
    /// <meta>mbr</meta>
    /// <status>Development</status>
    /// <summary>
    /// Backpropagate the deltas first from Gaussians to parameters (mu, sigma) and then from parameters to input
    /// </summary>
    /// <description></description>
    [Description("DeltaBack"), MyTaskInfo(OneShot = false)]
    public class MyGaussianBackDeltaTask : MyAbstractBackDeltaTask<MyGaussianHiddenLayer>
    {
        // Properties
        [YAXSerializableField(DefaultValue = true)]
        [MyBrowsable, Category("Regularization")]
        public bool Regularize { get; set; }

        // Properties
        [YAXSerializableField(DefaultValue = 0.01f)]
        [MyBrowsable, Category("Regularization")]
        public float RegularizationCoefficient { get; set; }

        public MyGaussianBackDeltaTask() { }

        private MyCudaKernel m_samplingDeltaKernel;
        private MyCudaKernel m_regularizationDeltaKernel;

        public override void Init(int nGPU)
        {
            m_samplingDeltaKernel = MyKernelFactory.Instance.Kernel(nGPU, @"NeuralNetwork\Layer\DeltaKernels", "GaussianSamplingDeltaKernel");
            m_regularizationDeltaKernel = MyKernelFactory.Instance.Kernel(nGPU, @"NeuralNetwork\Layer\RegularizationTermKernels", "GaussianRegularizationDeltaKernel");
        }

        public override void Execute()
        {
            // pointer to previous layer
            MyAbstractLayer previousLayer = Owner.PreviousLayer;

            if (previousLayer != null)
            {
                // reset delta
                previousLayer.Delta.Fill(0);
                if (Regularize) previousLayer.PreviousLayer.Delta.Fill(0);

                // determine input to previous layer
                CUdeviceptr prevInputPtr;
                if (previousLayer is MyAbstractWeightLayer)
                    prevInputPtr = (previousLayer as MyAbstractWeightLayer).NeuronInput.GetDevicePtr(previousLayer.GPU);
                else
                    prevInputPtr = previousLayer.Input.GetDevicePtr(previousLayer.GPU);

                m_samplingDeltaKernel.SetupExecution(previousLayer.Neurons);
                m_samplingDeltaKernel.Run(
                    Owner.Input,
                    Owner.Output,
                    previousLayer.Delta,
                    Owner.Delta,
                    Owner.RandomNormal,
                    Owner.Neurons
                );

                if (Regularize)
                {
                    m_regularizationDeltaKernel.SetConstantVariable<float>("RegularizationCoefficient", RegularizationCoefficient);
                    m_regularizationDeltaKernel.SetupExecution(previousLayer.Neurons);
                    m_regularizationDeltaKernel.Run(
                        previousLayer.Output,
                        previousLayer.Output.Count,
                        previousLayer.Input,
                        previousLayer.Input.Count,
                        (previousLayer as MyAbstractWeightLayer).Weights,
                        previousLayer.PreviousLayer.Delta
                    );
                }
            }
        }
    }

    ///// <author>GoodAI</author>
    ///// <meta>ph</meta>
    ///// <status>Working</status>
    ///// <summary>
    ///// Updates weights, that are fully connected to the previous layer.
    ///// </summary>
    ///// <description></description>
    //[Description("UpdateWeights"), MyTaskInfo(OneShot = false)]
    //public class MyGaussianUpdateWeightsTask : MyAbstractUpdateWeightsTask<MyAbstractWeightLayer>
    //{
    //    public MyGaussianUpdateWeightsTask() { } //parameterless constructor

    //    public override void Init(int nGPU) { }

    //    public override void Execute() //Task execution
    //    {
    //        //// get enabled loss function
    //        //MyTask task = Owner.ParentNetwork.GetEnabledTask("BackPropagation");
    //        //MyAbstractBackpropTask backpropTask = null;
    //        //if (task is MyAbstractBackpropTask)
    //        //    backpropTask = task as MyAbstractBackpropTask;
    //        //else
    //        //    MyLog.ERROR.WriteLine("Backprop task does not derive from MyAbstractBackpropTask in " + Owner.ParentNetwork);

    //        //if (backpropTask == null)
    //        //    MyLog.ERROR.WriteLine("Undetermined backprop task in " + Owner.ParentNetwork);
    //        //else
    //        //{
    //        //    backpropTask.Execute(Owner); // call the group task to do the backpropagation
    //        //}
    //    }
    //}
}
