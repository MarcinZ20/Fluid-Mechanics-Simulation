using UnityEngine;
using Unity.Mathematics;

public class Simulation2D : MonoBehaviour
{
    public event System.Action SimulationStepCompleted;

    [Header("Simulation Settings")]
    public float gravity;
    [Range(0, 1)] public float collisionDamping = 0.95f;
    public float smoothingRadius = 2;
    public float targetDensity;
    public float pressureMultiplier;
    public float nearPressureMultiplier;
    public float viscosityStrength;

    [Header("Container bounds")]
    public Vector2 boundsSize;

    [Header("Simulation helpers")]
    public ParticleSpawner spawner;
    public ComputeShader compute;
    public ParticleDisplay2D display;


    // Buffers
    public ComputeBuffer PositionBuffer { get; private set; }
    public ComputeBuffer VelocityBuffer { get; private set; }
    public ComputeBuffer DensityBuffer { get; private set; }
    ComputeBuffer predictedPositionBuffer;
    ComputeBuffer spatialIndices;
    ComputeBuffer spatialOffsets;
    GPUSort gpuSort;

    // Private Setting
    const int iterationsPerFrame = 3;
    const float timeScale = 1;

    // Kernel IDs
    const int externalForcesKernel = 0;
    const int spatialHashKernel = 1;
    const int densityKernel = 2;
    const int pressureKernel = 3;
    const int viscosityKernel = 4;
    const int updatePositionKernel = 5;

    // State
    bool isPaused;
    ParticleSpawner.ParticleSpawnData spawnData;
    bool pauseNextFrame;

    public int NumParticles { get; private set; }


    void Start()
    {
        Debug.Log("Controls:\n =====\nSpace -> Start/Stop\nR -> Reset\nNext frame -> N");

        float deltaTime = 1 / 60f;
        Time.fixedDeltaTime = deltaTime;

        spawnData = spawner.GetSpawnData();
        NumParticles = spawnData.positions.Length;

        // Create buffers
        PositionBuffer = ComputeHelper.CreateStructuredBuffer<float2>(NumParticles);
        predictedPositionBuffer = ComputeHelper.CreateStructuredBuffer<float2>(NumParticles);
        VelocityBuffer = ComputeHelper.CreateStructuredBuffer<float2>(NumParticles);
        DensityBuffer = ComputeHelper.CreateStructuredBuffer<float2>(NumParticles);
        spatialIndices = ComputeHelper.CreateStructuredBuffer<uint3>(NumParticles);
        spatialOffsets = ComputeHelper.CreateStructuredBuffer<uint>(NumParticles);

        // Set buffer data
        SetInitialBufferData(spawnData);

        // Initialize compute helper
        ComputeHelper.SetBuffer(compute, PositionBuffer, "Positions", externalForcesKernel, updatePositionKernel);
        ComputeHelper.SetBuffer(compute, predictedPositionBuffer, "PredictedPositions", externalForcesKernel, spatialHashKernel, densityKernel, pressureKernel, viscosityKernel);
        ComputeHelper.SetBuffer(compute, spatialIndices, "SpatialIndices", spatialHashKernel, densityKernel, pressureKernel, viscosityKernel);
        ComputeHelper.SetBuffer(compute, spatialOffsets, "SpatialOffsets", spatialHashKernel, densityKernel, pressureKernel, viscosityKernel);
        ComputeHelper.SetBuffer(compute, DensityBuffer, "Densities", densityKernel, pressureKernel, viscosityKernel);
        ComputeHelper.SetBuffer(compute, VelocityBuffer, "Velocities", externalForcesKernel, pressureKernel, viscosityKernel, updatePositionKernel);

        compute.SetInt("numParticles", NumParticles);

        gpuSort = new();
        gpuSort.SetBuffers(spatialIndices, spatialOffsets);

        display.Init(this);
    }

    void Update()
    {
        if (Time.frameCount > 10)
        {
            RunSimulationFrame(Time.deltaTime);
        }

        if (pauseNextFrame)
        {
            isPaused = true;
            pauseNextFrame = false;
        }

        HandleInput();
    }

    void RunSimulationFrame(float frameTime)
    {
        if (!isPaused)
        {
            float timeStep = frameTime / iterationsPerFrame * timeScale;

            UpdateSettings(timeStep);

            for (int i = 0; i < iterationsPerFrame; i++)
            {
                RunSimulationStep();
                SimulationStepCompleted?.Invoke();
            }
        }
    }

    void RunSimulationStep()
    {
        ComputeHelper.Dispatch(compute, NumParticles, kernelIndex: externalForcesKernel);
        ComputeHelper.Dispatch(compute, NumParticles, kernelIndex: spatialHashKernel);

        gpuSort.SortAndCalculateOffsets();

        ComputeHelper.Dispatch(compute, NumParticles, kernelIndex: densityKernel);
        ComputeHelper.Dispatch(compute, NumParticles, kernelIndex: pressureKernel);
        ComputeHelper.Dispatch(compute, NumParticles, kernelIndex: viscosityKernel);
        ComputeHelper.Dispatch(compute, NumParticles, kernelIndex: updatePositionKernel);

    }

    void UpdateSettings(float deltaTime)
    {
        compute.SetFloat("deltaTime", deltaTime);
        compute.SetFloat("gravity", -gravity);
        compute.SetFloat("collisionDamping", collisionDamping);
        compute.SetFloat("smoothingRadius", smoothingRadius);
        compute.SetFloat("targetDensity", targetDensity);
        compute.SetFloat("pressureMultiplier", pressureMultiplier);
        compute.SetFloat("nearPressureMultiplier", nearPressureMultiplier);
        compute.SetFloat("viscosityStrength", viscosityStrength);
        compute.SetVector("boundsSize", boundsSize);

        compute.SetFloat("Poly6ScalingFactor", 4 / (Mathf.PI * Mathf.Pow(smoothingRadius, 8)));
        compute.SetFloat("SpikyPow3ScalingFactor", 10 / (Mathf.PI * Mathf.Pow(smoothingRadius, 5)));
        compute.SetFloat("SpikyPow2ScalingFactor", 6 / (Mathf.PI * Mathf.Pow(smoothingRadius, 4)));
        compute.SetFloat("SpikyPow3DerivativeScalingFactor", 30 / (Mathf.Pow(smoothingRadius, 5) * Mathf.PI));
        compute.SetFloat("SpikyPow2DerivativeScalingFactor", 12 / (Mathf.Pow(smoothingRadius, 4) * Mathf.PI));
    }

    void SetInitialBufferData(ParticleSpawner.ParticleSpawnData spawnData)
    {
        float2[] allPoints = new float2[spawnData.positions.Length];
        System.Array.Copy(spawnData.positions, allPoints, spawnData.positions.Length);

        PositionBuffer.SetData(allPoints);
        predictedPositionBuffer.SetData(allPoints);
        VelocityBuffer.SetData(spawnData.velocities);
    }

    void HandleInput()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isPaused = !isPaused;
        }
        if (Input.GetKeyDown(KeyCode.N))
        {
            isPaused = false;
            pauseNextFrame = true;
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            isPaused = true;

            SetInitialBufferData(spawnData);
            RunSimulationStep();
            SetInitialBufferData(spawnData);
        }
    }

    void OnDestroy()
    {
        ComputeHelper.Release(PositionBuffer, predictedPositionBuffer, VelocityBuffer, DensityBuffer, spatialIndices, spatialOffsets);
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(230f / 255f, 0, 164f / 255f, 1f);
        Gizmos.DrawWireCube(Vector2.zero, boundsSize);
    }
}
