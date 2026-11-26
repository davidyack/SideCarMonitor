using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideCarMonitor.Mail
{
    /// <summary>
    /// Base type for a TypeSafe System One question. A question defines exactly one typed
    /// judgment about the shared state.
    /// </summary>
    /// <remarks>
    /// Question ids are never sent to the model, so the complete question always belongs in
    /// <see cref="Instructions"/> even when the id looks self-explanatory.
    /// </remarks>
    [JsonConverter(typeof(QuestionConverter))]
    public abstract class Question
    {
        /// <summary>Gets the discriminator sent to the API ("noul", "choice" or "score").</summary>
        public abstract string Type { get; }

        /// <summary>
        /// Gets the question itself. May be a string, or an object/array for structured
        /// instructions such as <c>{ question, inspect, focus }</c>.
        /// </summary>
        public object? Instructions { get; init; }
    }

    /// <summary>
    /// A yes/no question. The answer is a single probability that the answer is "yes".
    /// </summary>
    /// <remarks>
    /// Phrase instructions so a high probability means "yes". A Noul measures truth, not degree;
    /// use a <see cref="Score"/> when the answer sits on a spectrum.
    /// </remarks>
    public sealed class Noul : Question
    {
        /// <inheritdoc />
        public override string Type => "noul";

        /// <summary>
        /// Gets the optional boundary descriptions, serialized as <c>{ "true": ..., "false": ... }</c>.
        /// Use them when the yes/no boundary is subtle; never let "true" describe a "no".
        /// </summary>
        public NoulCriteria? Criteria { get; init; }
    }

    /// <summary>Describes what "yes" and "no" mean for a <see cref="Noul"/>.</summary>
    public sealed class NoulCriteria
    {
        /// <summary>Gets the description of the "yes" case (string, object or array).</summary>
        [JsonPropertyName("true")]
        public object? True { get; init; }

        /// <summary>Gets the description of the "no" case (string, object or array).</summary>
        [JsonPropertyName("false")]
        public object? False { get; init; }
    }

    /// <summary>
    /// A question whose answer is exactly one option from an unordered set of up to 255 options.
    /// </summary>
    /// <remarks>
    /// Option names and descriptions are both sent to the model, so write descriptions that
    /// separate confusable options, and always offer a catch-all such as "other".
    /// </remarks>
    public sealed class Choice : Question
    {
        /// <inheritdoc />
        public override string Type => "choice";

        /// <summary>
        /// Gets the options, mapping each option name to its description. A description may be a
        /// string, an object (for contrastive criteria) or null when the name speaks for itself.
        /// </summary>
        public required IReadOnlyDictionary<string, object?> Criteria { get; init; }
    }

    /// <summary>
    /// A question whose answer is a position on an ordered rubric of 2 to 10 levels.
    /// </summary>
    /// <remarks>
    /// Levels are evaluated independently, so each one must describe a situation. Relative wording
    /// ("worse than the previous level") and bare numbers do not work.
    /// </remarks>
    public sealed class Score : Question
    {
        /// <inheritdoc />
        public override string Type => "score";

        /// <summary>Gets the ordered level descriptions, lowest first. Level numbers are array indexes.</summary>
        public required IReadOnlyList<object> Criteria { get; init; }
    }

    /// <summary>Writes <see cref="Question"/> instances in the shape the API expects.</summary>
    internal sealed class QuestionConverter : JsonConverter<Question>
    {
        /// <inheritdoc />
        public override Question? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("Questions are only ever serialized.");

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, Question value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.Type);

            if (value.Instructions is not null)
            {
                writer.WritePropertyName("instructions");
                JsonSerializer.Serialize(writer, value.Instructions, value.Instructions.GetType(), options);
            }

            switch (value)
            {
                case Noul { Criteria: not null } noul:
                    writer.WritePropertyName("criteria");
                    JsonSerializer.Serialize(writer, noul.Criteria, options);
                    break;

                case Choice choice:
                    writer.WritePropertyName("criteria");
                    writer.WriteStartObject();
                    foreach (var option in choice.Criteria)
                    {
                        writer.WritePropertyName(option.Key);
                        if (option.Value is null)
                            writer.WriteNullValue();
                        else
                            JsonSerializer.Serialize(writer, option.Value, option.Value.GetType(), options);
                    }
                    writer.WriteEndObject();
                    break;

                case Score score:
                    writer.WritePropertyName("criteria");
                    writer.WriteStartArray();
                    foreach (var level in score.Criteria)
                        JsonSerializer.Serialize(writer, level, level.GetType(), options);
                    writer.WriteEndArray();
                    break;
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>The body posted to <c>POST /v1/systemone</c>.</summary>
    internal sealed class SystemOneRequest
    {
        /// <summary>Gets the single state that every question is evaluated against.</summary>
        [JsonPropertyName("state")]
        public required object State { get; init; }

        /// <summary>Gets the model id, for example <c>jev-1.13.0</c>.</summary>
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        /// <summary>Gets the questions, keyed by the ids answers come back under.</summary>
        [JsonPropertyName("questions")]
        public required IReadOnlyDictionary<string, Question> Questions { get; init; }
    }

    /// <summary>Base type for a typed answer to one question.</summary>
    public abstract class Answer
    {
        /// <summary>Gets the answer's discriminator.</summary>
        public abstract string Type { get; }
    }

    /// <summary>The answer to a <see cref="Noul"/>: the probability that the answer is "yes".</summary>
    public sealed class NoulAnswer : Answer
    {
        /// <inheritdoc />
        public override string Type => "noul";

        /// <summary>Gets the probability, from 0 (certainly no) to 1 (certainly yes).</summary>
        public double Value { get; init; }
    }

    /// <summary>The answer to a <see cref="Choice"/>.</summary>
    public sealed class ChoiceAnswer : Answer
    {
        /// <inheritdoc />
        public override string Type => "choice";

        /// <summary>Gets the highest-probability option.</summary>
        public required string Selected { get; init; }

        /// <summary>Gets how peaked the distribution is, from 0 (flat) to 1 (certain).</summary>
        public double Confidence { get; init; }

        /// <summary>Gets the probability of every option. Sums to 1.</summary>
        public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

        /// <summary>Gets the probability assigned to <paramref name="option"/>, or 0 if it is absent.</summary>
        /// <param name="option">The option name.</param>
        public double ProbabilityOf(string option) => Probabilities.TryGetValue(option, out var p) ? p : 0d;
    }

    /// <summary>The answer to a <see cref="Score"/>.</summary>
    public sealed class ScoreAnswer : Answer
    {
        /// <inheritdoc />
        public override string Type => "score";

        /// <summary>
        /// Gets the probability-weighted level number. It may be fractional, and it is a position
        /// on the rubric rather than a measured quantity, so read it with <see cref="Probabilities"/>.
        /// </summary>
        public double Value { get; init; }

        /// <summary>Gets how peaked the distribution is, from 0 (flat) to 1 (certain).</summary>
        public double Confidence { get; init; }

        /// <summary>Gets the probability of each level, keyed by level number.</summary>
        public required IReadOnlyDictionary<int, double> Probabilities { get; init; }

        /// <summary>Gets the nearest whole level, for decisions that map a level to an action.</summary>
        public int NearestLevel => (int)Math.Round(Value, MidpointRounding.AwayFromZero);

        /// <summary>
        /// Gets the score on a 0 to 1 range, which is required before combining rubrics of
        /// different lengths.
        /// </summary>
        /// <param name="levelCount">The number of levels the question defined.</param>
        public double Normalized(int levelCount) => levelCount > 1 ? Value / (levelCount - 1) : 0d;
    }

    /// <summary>The result of one System One evaluation.</summary>
    public sealed class SystemOneResponse
    {
        /// <summary>Gets the versioned model id that actually answered. Worth logging, since aliases move.</summary>
        public required string Model { get; init; }

        /// <summary>Gets one answer per question id.</summary>
        public required IReadOnlyDictionary<string, Answer> Answers { get; init; }

        /// <summary>Gets the billed input token count, when the API reported one.</summary>
        public int? InputTokens { get; init; }

        /// <summary>Gets the <c>x-typesafe-request-id</c> header value, for support and tracing.</summary>
        public string? RequestId { get; init; }

        /// <summary>Gets the Noul answer for <paramref name="id"/>, or null when it is absent.</summary>
        /// <param name="id">The question id.</param>
        public NoulAnswer? Noul(string id) => Answers.TryGetValue(id, out var a) ? a as NoulAnswer : null;

        /// <summary>Gets the Choice answer for <paramref name="id"/>, or null when it is absent.</summary>
        /// <param name="id">The question id.</param>
        public ChoiceAnswer? Choice(string id) => Answers.TryGetValue(id, out var a) ? a as ChoiceAnswer : null;

        /// <summary>Gets the Score answer for <paramref name="id"/>, or null when it is absent.</summary>
        /// <param name="id">The question id.</param>
        public ScoreAnswer? Score(string id) => Answers.TryGetValue(id, out var a) ? a as ScoreAnswer : null;

        /// <summary>
        /// Gets the probability for <paramref name="id"/>, or <paramref name="fallback"/> when the
        /// question was not answered, so composition code stays free of null checks.
        /// </summary>
        /// <param name="id">The question id.</param>
        /// <param name="fallback">The value to assume when the answer is missing.</param>
        public double NoulValue(string id, double fallback = 0d) => Noul(id)?.Value ?? fallback;
    }

    /// <summary>Thrown when the TypeSafe API cannot be reached or returns a non-success status.</summary>
    public sealed class TypeSafeApiException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="TypeSafeApiException"/> class.</summary>
        /// <param name="message">The error message.</param>
        /// <param name="status">The HTTP status code, when there was a response.</param>
        /// <param name="requestId">The <c>x-typesafe-request-id</c> header, when present.</param>
        /// <param name="inner">The underlying exception, when there was one.</param>
        public TypeSafeApiException(string message, int? status = null, string? requestId = null, Exception? inner = null)
            : base(message, inner)
        {
            Status = status;
            RequestId = requestId;
        }

        /// <summary>Gets the HTTP status code, when the failure came with a response.</summary>
        public int? Status { get; }

        /// <summary>Gets the request id reported by the API, for support and tracing.</summary>
        public string? RequestId { get; }
    }
}
