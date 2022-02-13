namespace OpenTabletDriver.Output
{
    /// <summary>
    /// A pipeline element with a predefined position within the pipeline.
    /// </summary>
    /// <typeparam name="T">
    /// The pipeline element type.
    /// </typeparam>
    public interface IPositionedPipelineElement<T> : IPipelineElement<T>
    {
        /// <summary>
        /// The position in which this <see cref="IPipelineElement{T}"/> will be processed.
        /// This helps determine what the expected input units will be.
        /// </summary>
        PipelinePosition Position { get; }
    }
}
