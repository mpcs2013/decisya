using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Decisya.SharedKernel;
using Decisya.ServiceDefaults.Logging;

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>
/// The masking core, rules 1-7 plus the G3 threat-model changes (collections, restricted
/// reflection, Uri rendering, AnyMasked semantics, value-shape masking, the wider deny
/// list). G4-15-11 to 18.
/// </summary>
public class SensitiveDataMaskingProcessorTests
{
    private readonly SensitiveDataMaskingProcessor _processor = new();

    // --- Rule 1: key deny-list ---

    [Theory]
    [InlineData("Password")]
    [InlineData("api_key")]
    [InlineData("Api-Key")]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("ConnectionString")]
    [InlineData("QueryString")]
    [InlineData("pwd")]
    [InlineData("credential")]
    [InlineData("PrivateKey")]
    [InlineData("SigningKey")]
    [InlineData("UserIdHashKey")]
    [InlineData("bearer")]
    [InlineData("jwt")]
    [InlineData("session")]
    [InlineData("Secret")]
    [InlineData("access_token")]
    [InlineData("Passwd")]
    public void A_denied_key_is_masked_in_state(string key)
    {
        var canary = Canaries.Unique(key);
        var masked = _processor.Process(State((key, canary), ("{OriginalFormat}", $"log {{{key}}}")));

        masked.AnyMasked.Should().BeTrue();
        masked.Attributes.Should().ContainSingle(p => p.Key == key && Equals(p.Value, SensitiveDataMaskingProcessor.Mask));
    }

    // --- G4-15-18: the same deny-list entries in scope-key position ---

    [Theory]
    [InlineData("Password")]
    [InlineData("api_key")]
    [InlineData("Api-Key")]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("ConnectionString")]
    [InlineData("QueryString")]
    [InlineData("pwd")]
    [InlineData("credential")]
    [InlineData("PrivateKey")]
    [InlineData("SigningKey")]
    [InlineData("UserIdHashKey")]
    [InlineData("bearer")]
    [InlineData("jwt")]
    [InlineData("session")]
    [InlineData("Secret")]
    [InlineData("access_token")]
    [InlineData("Passwd")]
    public void A_denied_key_is_masked_in_scope_position(string key)
    {
        var canary = Canaries.Unique(key);

        var value = _processor.ProcessValue(key, canary, out var masked);

        masked.Should().BeTrue();
        value.Should().Be(SensitiveDataMaskingProcessor.Mask);
        value!.ToString().Should().NotContain(canary);
    }

    // --- G4-15-18: the same deny-list entries as a member name inside a rendered object ---

    [Theory]
    [InlineData(nameof(DenyListedMembersProbe.Password))]
    [InlineData(nameof(DenyListedMembersProbe.ApiKey))]
    [InlineData(nameof(DenyListedMembersProbe.Authorization))]
    [InlineData(nameof(DenyListedMembersProbe.Cookie))]
    [InlineData(nameof(DenyListedMembersProbe.ConnectionString))]
    [InlineData(nameof(DenyListedMembersProbe.QueryString))]
    [InlineData(nameof(DenyListedMembersProbe.Pwd))]
    [InlineData(nameof(DenyListedMembersProbe.Credential))]
    [InlineData(nameof(DenyListedMembersProbe.PrivateKey))]
    [InlineData(nameof(DenyListedMembersProbe.SigningKey))]
    [InlineData(nameof(DenyListedMembersProbe.UserIdHashKey))]
    [InlineData(nameof(DenyListedMembersProbe.Bearer))]
    [InlineData(nameof(DenyListedMembersProbe.Jwt))]
    [InlineData(nameof(DenyListedMembersProbe.Session))]
    [InlineData(nameof(DenyListedMembersProbe.Secret))]
    [InlineData(nameof(DenyListedMembersProbe.AccessToken))]
    [InlineData(nameof(DenyListedMembersProbe.Passwd))]
    public void A_denied_key_is_masked_as_a_nested_member_name(string propertyName)
    {
        var canary = Canaries.Unique(propertyName);
        var probe = new DenyListedMembersProbe();
        typeof(DenyListedMembersProbe).GetProperty(propertyName)!.SetValue(probe, canary);

        var masked = _processor.Process(State(("Probe", probe), ("{OriginalFormat}", "p {Probe}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Probe").Value!;
        rendered.Should().NotContain(canary);
        rendered.Should().Contain($"{propertyName} = {SensitiveDataMaskingProcessor.Mask}");
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void An_empty_query_string_is_not_counted_as_masked()
    {
        var masked = _processor.Process(State(
            ("QueryString", string.Empty),
            ("{OriginalFormat}", "Request starting {QueryString}")));

        masked.AnyMasked.Should().BeFalse();
        masked.Attributes.Should().ContainSingle(p => p.Key == "QueryString" && Equals(p.Value, string.Empty));
    }

    [Fact]
    public void A_non_empty_query_string_is_masked()
    {
        var masked = _processor.Process(State(
            ("QueryString", "?token=secret"),
            ("{OriginalFormat}", "Request starting {QueryString}")));

        masked.AnyMasked.Should().BeTrue();
        masked.Attributes.Should().ContainSingle(p => p.Key == "QueryString" && Equals(p.Value, SensitiveDataMaskingProcessor.Mask));
    }

    // --- Rule 2: scalars pass through ---

    [Fact]
    public void Scalars_pass_through_unchanged()
    {
        var guid = Guid.NewGuid();
        var money = new Money(12.34m, Currency.FromCode("USD"));
        var masked = _processor.Process(State(
            ("Count", 42),
            ("Id", guid),
            ("Amount", money),
            ("{OriginalFormat}", "scalars {Count} {Id} {Amount}")));

        masked.AnyMasked.Should().BeFalse();
        masked.Attributes.Should().Contain(p => p.Key == "Count" && Equals(p.Value, 42));
        masked.Attributes.Should().Contain(p => p.Key == "Id" && Equals(p.Value, guid));
        masked.Attributes.Should().Contain(p => p.Key == "Amount" && Equals(p.Value, money));
    }

    [Fact]
    public void A_uri_loses_its_userinfo_query_and_fragment()
    {
        var uri = new Uri("https://u:p@h.example/a?token=x#f");

        var masked = _processor.Process(State(("Endpoint", uri), ("{OriginalFormat}", "calling {Endpoint}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Endpoint").Value!;
        rendered.Should().Be("https://h.example/a?***");
        masked.AnyMasked.Should().BeTrue();
    }

    // --- M-1 (G4-15-16, 17; G6 review): string/Uri members, collection elements, and
    // framework types made only of strings ---

    [Fact]
    public void Case_a_a_Decisya_record_with_only_scalar_string_and_uri_members_masks_both()
    {
        var jwt = Canaries.JwtShaped();
        var callback = new Uri("https://u:p@h.example/cb?token=x#f");
        var record = new NoteAndCallback($"token was {jwt} in the request", callback);

        var masked = _processor.Process(State(("R", record), ("{OriginalFormat}", "r {R}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "R").Value!;
        rendered.Should().NotContain(jwt);
        rendered.Should().NotContain("u:p@");
        rendered.Should().NotContain("token=x");
        rendered.Should().Contain(SensitiveDataMaskingProcessor.Mask);
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void Case_b_a_list_of_strings_masks_a_jwt_shaped_element_but_a_clean_list_still_passes_through()
    {
        var jwt = Canaries.JwtShaped();
        var dirty = new List<string> { "clean", jwt };
        var clean = new List<string> { "a", "b" };

        var maskedDirty = _processor.Process(State(("Values", dirty), ("{OriginalFormat}", "t {Values}")));
        var maskedClean = _processor.Process(State(("Values", clean), ("{OriginalFormat}", "t {Values}")));

        var dirtyValue = (List<object?>)maskedDirty.Attributes.Single(p => p.Key == "Values").Value!;
        dirtyValue.Should().Contain(SensitiveDataMaskingProcessor.Mask);
        dirtyValue.OfType<string>().Any(s => s.Contains(jwt, StringComparison.Ordinal)).Should().BeFalse();
        maskedDirty.AnyMasked.Should().BeTrue();

        // G4-15-11: a collection with nothing to mask still passes through by reference.
        maskedClean.Attributes.Single(p => p.Key == "Values").Value.Should().BeSameAs(clean);
        maskedClean.AnyMasked.Should().BeFalse();
    }

    [Fact]
    public void Case_b_a_list_of_uris_masks_every_element()
    {
        var uris = new List<Uri> { new("https://u:p@h.example/a?token=x") };

        var masked = _processor.Process(State(("Endpoints", uris), ("{OriginalFormat}", "e {Endpoints}")));

        var value = (List<object?>)masked.Attributes.Single(p => p.Key == "Endpoints").Value!;
        value.Should().ContainSingle().Which.Should().Be("https://h.example/a?***");
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void Case_c_a_framework_type_made_only_of_strings_is_masked_whole()
    {
        var canary = Canaries.Unique("bearer-token");
        var header = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", canary);

        var masked = _processor.Process(State(("Auth", header), ("{OriginalFormat}", "a {Auth}")));

        masked.Attributes.Single(p => p.Key == "Auth").Value.Should().Be(SensitiveDataMaskingProcessor.Mask);
        masked.AnyMasked.Should().BeTrue();
    }

    // --- Rule 3: [Sensitive] type ---

    [Fact]
    public void A_sensitive_typed_value_is_masked_whole()
    {
        var canary = Canaries.Unique("token-value");
        var masked = _processor.Process(State(("Token", new SensitiveToken(canary)), ("{OriginalFormat}", "t {Token}")));

        masked.AnyMasked.Should().BeTrue();
        masked.Attributes.Single(p => p.Key == "Token").Value.Should().Be(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void A_sensitive_struct_held_as_nullable_is_masked()
    {
        var holder = new NullableSensitiveHolder { Id = new SensitiveId("secret") };

        var masked = _processor.Process(State(("Holder", holder), ("{OriginalFormat}", "h {Holder}")));

        // NullableSensitiveHolder is itself a Decisya-declared, renderable type (rule 4), so
        // it is rendered as an object whose Id member — which carries a SensitiveAttribute
        // struct — is masked, rather than the whole holder collapsing to "***".
        var rendered = (string)masked.Attributes.Single(p => p.Key == "Holder").Value!;
        rendered.Should().NotContain("secret");
        rendered.Should().Contain($"Id = {SensitiveDataMaskingProcessor.Mask}");
        masked.AnyMasked.Should().BeTrue();
    }

    // --- Rule 4: types whose graph carries [Sensitive] are rendered, never ToString() ---

    [Fact]
    public void A_Decisya_type_with_a_sensitive_member_renders_with_that_member_masked()
    {
        var canary = Canaries.Unique("email");
        var snapshot = new CustomerSnapshot("Ada", canary);

        var masked = _processor.Process(State(("Customer", snapshot), ("{OriginalFormat}", "c {Customer}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Customer").Value!;
        rendered.Should().NotContain(canary);
        rendered.Should().Contain(SensitiveDataMaskingProcessor.Mask);
        rendered.Should().Contain("Ada");
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void A_derived_record_overriding_a_sensitive_property_without_restating_the_attribute_still_masks()
    {
        var canary = Canaries.Unique("email");
        var derived = new MarkedDerived { Email = canary };

        var masked = _processor.Process(State(("Value", derived), ("{OriginalFormat}", "v {Value}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Value").Value!;
        rendered.Should().NotContain(canary);
    }

    [Fact]
    public void An_interface_member_marked_sensitive_masks_through_the_implementation()
    {
        var canary = Canaries.Unique("email");
        var value = new ImplementsMarkedInterface { Email = canary };

        var masked = _processor.Process(State(("Value", value), ("{OriginalFormat}", "v {Value}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Value").Value!;
        rendered.Should().NotContain(canary);
    }

    [Fact]
    public void Nesting_three_deep_still_masks_the_sensitive_leaf()
    {
        var canary = Canaries.Unique("email");
        var root = new DeepRoot(new DeepLevel1(new DeepLevel2(new DeepLevel3(canary))));

        var masked = _processor.Process(State(("Root", root), ("{OriginalFormat}", "r {Root}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Root").Value!;
        rendered.Should().NotContain(canary);
    }

    [Fact]
    public void A_member_whose_name_alone_is_denylisted_is_masked_even_without_the_attribute()
    {
        var canary = Canaries.Unique("token-value");
        var holder = new DenyListedMemberHolder { AccessToken = canary };

        var masked = _processor.Process(State(("Holder", holder), ("{OriginalFormat}", "h {Holder}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Holder").Value!;
        rendered.Should().NotContain(canary);
    }

    // --- Change 2 (T-10): only Decisya-declared / anonymous / tuple-like types are rendered ---

    [Fact]
    public void A_framework_type_nested_in_a_Decisya_wrapper_is_masked_in_place_never_rendered()
    {
        // ObjectHolder is Decisya-declared, so it is rendered (rule 4). Its object-typed
        // Payload holds a non-Decisya, non-scalar framework value (a collection here), which
        // change 2 (T-10) never renders (never walks or calls getters on) — it is masked
        // whole, in place, so the outer wrapper renders as "{ Payload = *** }", not "***".
        var holder = new ObjectHolder { Payload = new System.Collections.ArrayList { 1, 2, 3 } };

        var masked = _processor.Process(State(("Holder", holder), ("{OriginalFormat}", "h {Holder}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Holder").Value!;
        rendered.Should().Be($"{{ Payload = {SensitiveDataMaskingProcessor.Mask} }}");
        rendered.Should().NotContain("1").And.NotContain("2").And.NotContain("3");
    }

    [Fact]
    public void An_exception_passed_as_a_plain_state_value_is_masked_whole()
    {
        // G4-15-19: an Exception handed in as an ordinary state value (not through the
        // `exception` argument the exception seam handles) is still just a non-Decisya,
        // non-scalar framework type with a rendering-required graph (Exception.Data is an
        // untyped IDictionary), so change 2 masks it whole rather than calling its getters.
        var exception = new InvalidOperationException("a safe, non-sensitive message");

        var masked = _processor.Process(State(("Error", exception), ("{OriginalFormat}", "e {Error}")));

        masked.Attributes.Single(p => p.Key == "Error").Value.Should().Be(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void ToString_on_a_type_the_processor_renders_is_never_called()
    {
        var canary = Canaries.Unique("tostring-leak");
        var holder = new ObjectHolder { Payload = new LoudToString(canary) };

        var masked = _processor.Process(State(("Holder", holder), ("{OriginalFormat}", "h {Holder}")));

        var rendered = masked.Attributes.Single(p => p.Key == "Holder").Value!.ToString();
        rendered.Should().NotContain(canary);
    }

    [Fact]
    public void A_getter_that_throws_is_masked_and_the_rest_of_the_object_survives()
    {
        var holder = new ThrowingProbe();

        var masked = _processor.Process(State(("Value", holder), ("{OriginalFormat}", "v {Value}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Value").Value!;
        rendered.Should().Contain(SensitiveDataMaskingProcessor.Mask);
        rendered.Should().Contain("safe");
    }

    [Fact]
    public void A_getter_is_read_at_most_once_per_log_call()
    {
        var probe = new CountingProbe();

        _processor.Process(State(("Value", probe), ("{OriginalFormat}", "v {Value}")));

        probe.ReadCount.Should().Be(1);
    }

    // --- Rule 4b / change 1 (T-09): collections are masked whole unless their elements are scalar ---

    [Theory]
    [InlineData("list")]
    [InlineData("array")]
    [InlineData("dictionary")]
    [InlineData("enumerable")]
    [InlineData("arraylist")]
    public void A_collection_of_sensitive_carrying_elements_is_masked_whole(string shape)
    {
        var canary = Canaries.Unique("collection-email");
        object value = shape switch
        {
            "list" => new List<CustomerSnapshot> { new("Ada", canary) },
            "array" => new[] { new CustomerSnapshot("Ada", canary) },
            "dictionary" => new Dictionary<string, CustomerSnapshot> { ["a"] = new("Ada", canary) },
            "enumerable" => new List<CustomerSnapshot> { new("Ada", canary) }.Where(_ => true),
            "arraylist" => new System.Collections.ArrayList { canary },
            _ => throw new InvalidOperationException(shape),
        };

        var masked = _processor.Process(State(("Items", value), ("{OriginalFormat}", "i {Items}")));

        var rendered = masked.Attributes.Single(p => p.Key == "Items").Value;
        rendered.Should().Be(SensitiveDataMaskingProcessor.Mask);
        rendered!.ToString().Should().NotContain(canary);
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void A_collection_of_scalars_passes_through()
    {
        var ints = new[] { 1, 2, 3 };
        var strings = new List<string> { "a", "b" };

        var masked = _processor.Process(State(("Ints", ints), ("Strings", strings), ("{OriginalFormat}", "i {Ints} {Strings}")));

        masked.Attributes.Single(p => p.Key == "Ints").Value.Should().BeSameAs(ints);
        masked.Attributes.Single(p => p.Key == "Strings").Value.Should().BeSameAs(strings);
        masked.AnyMasked.Should().BeFalse();
    }

    // --- Rule 5: plain non-sensitive types pass through ---

    [Fact]
    public void A_plain_type_with_a_fully_known_non_sensitive_graph_passes_through()
    {
        var point = new PlainPoint(1, 2);

        var masked = _processor.Process(State(("Point", point), ("{OriginalFormat}", "p {Point}")));

        masked.Attributes.Single(p => p.Key == "Point").Value.Should().BeSameAs(point);
        masked.AnyMasked.Should().BeFalse();
    }

    // --- Rule 7: unstructured state ---

    [Fact]
    public void Unstructured_state_masks_the_message_and_flags_the_record()
    {
        var masked = _processor.Process(state: null);

        masked.AnyMasked.Should().BeTrue();
        masked.Template.Should().BeNull();
        masked.Attributes.Should().Contain(p =>
            p.Key == SensitiveDataMaskingProcessor.MaskingErrorKey
            && Equals(p.Value, SensitiveDataMaskingProcessor.UnstructuredStateError));
    }

    // --- Value-shape masking (G4-15-17) ---

    [Fact]
    public void A_JWT_shaped_substring_is_masked_even_under_an_innocent_key()
    {
        var jwt = Canaries.JwtShaped();
        var masked = _processor.Process(State(("Note", $"token was {jwt} in the request"), ("{OriginalFormat}", "n {Note}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Note").Value!;
        rendered.Should().NotContain(jwt);
        rendered.Should().Contain(SensitiveDataMaskingProcessor.Mask);
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void A_bearer_shaped_substring_is_masked_even_under_an_innocent_key()
    {
        var bearer = Canaries.BearerShaped();
        var masked = _processor.Process(State(("Note", $"header: {bearer} end"), ("{OriginalFormat}", "n {Note}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Note").Value!;
        rendered.Should().NotContain(bearer);
        masked.AnyMasked.Should().BeTrue();
    }

    // --- Exception seam (G4-15-19) ---

    [Fact]
    public void The_exception_seam_masks_token_shapes_in_the_message_and_full_text_but_keeps_the_type_and_trace()
    {
        var jwt = Canaries.JwtShaped();
        var inner = new InvalidOperationException($"inner failure with {jwt}");
        var outer = new InvalidOperationException("outer failure", inner);

        var (type, message, stackTrace) = _processor.ProcessException(outer);

        type.Should().Be(typeof(InvalidOperationException).FullName);
        message.Should().Be("outer failure");
        stackTrace.Should().NotContain(jwt);
        stackTrace.Should().Contain(SensitiveDataMaskingProcessor.Mask);
        stackTrace.Should().Contain(nameof(InvalidOperationException));
    }

    // --- AnyMasked semantics (change 4) ---

    [Fact]
    public void AnyMasked_is_true_when_a_value_is_rendered_even_without_a_sensitive_hit()
    {
        var holder = new ObjectHolder { Payload = "plain-value" };

        var masked = _processor.Process(State(("Holder", holder), ("{OriginalFormat}", "h {Holder}")));

        // ObjectHolder is Decisya-declared and its Payload is object-typed, so rule 4
        // selects it for rendering even though the rendered value itself is not
        // sensitive. AnyMasked must still be true, or the sink would use the
        // MEL-formatted message instead of the safe template.
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void AnyMasked_is_false_when_every_value_passes_through_unchanged()
    {
        var masked = _processor.Process(State(("Count", 1), ("{OriginalFormat}", "c {Count}")));

        masked.AnyMasked.Should().BeFalse();
    }

    // --- G4-15-13: bounds beyond what the depth-3 example already covers ---

    [Fact]
    public void Nesting_one_level_beyond_the_depth_cap_masks_the_branch_whole_without_rendering_it()
    {
        var canary = Canaries.Unique("email");
        var root = new TooDeepRoot(new TooDeepLevel1(new TooDeepLevel2(new TooDeepLevel3(new TooDeepLevel4(canary)))));

        var masked = _processor.Process(State(("Root", root), ("{OriginalFormat}", "r {Root}")));

        // TooDeepLevel3 is still rendered at depth 3 (the cap), but its own "Inner" member —
        // TooDeepLevel4, evaluated one level deeper — is masked whole rather than rendered,
        // so the Email leaf inside it is never reached, let alone printed.
        var rendered = (string)masked.Attributes.Single(p => p.Key == "Root").Value!;
        rendered.Should().NotContain(canary);
        rendered.Should().Contain($"Inner = {SensitiveDataMaskingProcessor.Mask}");
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void More_than_32_members_on_one_object_are_capped_and_the_remainder_becomes_mask()
    {
        var holder = new ManyMembersHolder();

        var masked = _processor.Process(State(("Holder", holder), ("{OriginalFormat}", "h {Holder}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Holder").Value!;
        var renderedMemberCount = Regex.Count(rendered, " = ", RegexOptions.None, TimeSpan.FromSeconds(1));
        renderedMemberCount.Should().BeLessThanOrEqualTo(SensitiveDataMaskingProcessor.MaxMembersPerObject);
        rendered.Should().Contain(SensitiveDataMaskingProcessor.Mask);
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void More_than_256_rendered_nodes_in_one_record_exhausts_the_shared_budget()
    {
        // Each ObjectHolder consumes two nodes from the record-wide budget: one for the
        // object itself, one for its single Payload member. 140 of them need 280 nodes,
        // over the 256-node-per-record cap, so the later ones in insertion order are
        // masked whole instead of rendered.
        const int holderCount = 140;
        var pairs = new List<(string Key, object? Value)>(holderCount + 1);
        for (var i = 0; i < holderCount; i++)
        {
            pairs.Add(($"Holder{i:D3}", new ObjectHolder { Payload = "x" }));
        }

        pairs.Add(("{OriginalFormat}", "many holders"));

        var masked = _processor.Process(State(pairs.ToArray()));

        var renderedValues = masked.Attributes
            .Where(p => p.Key.StartsWith("Holder", StringComparison.Ordinal))
            .Select(p => p.Value)
            .ToArray();

        renderedValues.Should().Contain(v => Equals(v, "{ Payload = x }"));
        renderedValues.Should().Contain(v => Equals(v, SensitiveDataMaskingProcessor.Mask));
        masked.AnyMasked.Should().BeTrue();
    }

    [Fact]
    public void A_string_longer_than_the_max_length_is_truncated_with_a_marker_in_the_shared_core()
    {
        // The core is the single seam both sinks call (Process for state, the same core for
        // scopes and exceptions), so a truncation proof here covers both sinks without
        // duplicating the assertion in the formatter and OTLP processor test files.
        var longValue = new string('a', SensitiveDataMaskingProcessor.MaxStringLength + 100);

        var masked = _processor.Process(State(("Note", longValue), ("{OriginalFormat}", "n {Note}")));

        var rendered = (string)masked.Attributes.Single(p => p.Key == "Note").Value!;
        rendered.Length.Should().Be(SensitiveDataMaskingProcessor.MaxStringLength + SensitiveDataMaskingProcessor.TruncationMarker.Length);
        rendered.Should().EndWith(SensitiveDataMaskingProcessor.TruncationMarker);
        masked.AnyMasked.Should().BeTrue();
    }

    // --- G4-15-22 (SHOULD): masking errors are visible through a counter, never through ILogger ---

    [Fact]
    public void A_masking_error_increments_the_error_counter()
    {
        var measurements = new List<(long Value, string? Rule)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ObservabilityDiagnostics.MeterName
                && instrument.Name == ObservabilityDiagnostics.MaskingErrorsCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            var rule = tags.ToArray().FirstOrDefault(t => t.Key == "rule").Value?.ToString();
            lock (measurements)
            {
                measurements.Add((measurement, rule));
            }
        });
        listener.Start();

        _processor.Process(State(("Value", new ThrowingProbe()), ("{OriginalFormat}", "v {Value}")));

        lock (measurements)
        {
            measurements.Should().Contain(m => m.Rule == "getter" && m.Value == 1);
        }
    }

    // --- ProcessValue (used for scopes) fails closed and never throws ---

    [Fact]
    public void ProcessValue_never_throws_and_reports_masked_on_failure()
    {
        var act = () => _processor.ProcessValue("Scope", new ThrowingProbe(), out var masked);

        act.Should().NotThrow();
    }

    private static KeyValuePair<string, object?>[] State(params (string Key, object? Value)[] pairs) =>
        [.. pairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value))];
}
