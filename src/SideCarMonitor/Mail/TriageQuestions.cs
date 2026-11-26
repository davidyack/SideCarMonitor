namespace SideCarMonitor.Mail
{
    /// <summary>
    /// Every TypeSafe question and every threshold used by <see cref="MailManagementAgentV4"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These constants are the whole decision boundary of V4, deliberately kept in one file so they
    /// can be read and edited without touching the orchestration code. Tune the thresholds against
    /// labelled email data by plotting confidence against accuracy; they are starting points, not
    /// measured values.
    /// </para>
    /// <para>
    /// Design notes: each question judges exactly one property, the confusable categories carry
    /// contrastive <c>what</c> / <c>not_for</c> / <c>examples</c> criteria, and nothing here asks the
    /// model to do anything code can do exactly (domain matching, flag status, sender history).
    /// </para>
    /// </remarks>
    public static class TriageQuestions
    {
        // ---------------------------------------------------------------------
        // Question ids
        // ---------------------------------------------------------------------

        /// <summary>Id of the primary category Choice.</summary>
        public const string Category = "primary_category";

        /// <summary>Id of the Noul detecting an intent to sell.</summary>
        public const string SalesIntent = "sales_intent";

        /// <summary>Id of the Noul detecting recurring editorial content.</summary>
        public const string RecurringNewsletter = "recurring_newsletter";

        /// <summary>Id of the Noul detecting a genuine invitation to attend something.</summary>
        public const string InvitesToAttend = "invites_to_attend";

        /// <summary>Id of the Noul confirming the email concerns an order already placed.</summary>
        public const string OrderAlreadyPlaced = "order_already_placed";

        /// <summary>Id of the Noul detecting an outstanding balance the recipient owes.</summary>
        public const string BalanceDue = "balance_due";

        /// <summary>Id of the Noul detecting that the recipient personally must do something.</summary>
        public const string RequiresRecipientAction = "requires_recipient_action";

        /// <summary>Id of the Score rating how substantive a distribution list post is.</summary>
        public const string ListSubstance = "list_substance";

        /// <summary>Id of the Noul comparing the claimed sender identity against its domain.</summary>
        public const string SenderDomainMismatch = "sender_domain_mismatch";

        /// <summary>Id of the Noul detecting manufactured time pressure.</summary>
        public const string UrgencyPressure = "urgency_pressure";

        /// <summary>Id of the Noul detecting a request for credentials or an unexpected payment.</summary>
        public const string CredentialOrPaymentRequest = "credential_or_payment_request";

        /// <summary>Id of the Noul comparing link destinations against the claimed sender.</summary>
        public const string LinkDestinationMismatch = "link_destination_mismatch";

        /// <summary>Id of the Noul detecting impersonal boilerplate with no specifics.</summary>
        public const string ImpersonalBoilerplate = "impersonal_boilerplate";

        /// <summary>Id of the Noul deciding whether a one-line summary loses important content.</summary>
        public const string WarrantsExtendedSummary = "warrants_extended_summary";

        /// <summary>Id of the Noul deciding whether the recipient personally needs to see this.</summary>
        public const string NeedsPersonalAttention = "needs_personal_attention";

        /// <summary>Prefix for the dynamically built questions, one per caller-supplied rule.</summary>
        public const string RulePrefix = "rule_";

        // ---------------------------------------------------------------------
        // Thresholds
        // ---------------------------------------------------------------------

        /// <summary>
        /// Below this Choice confidence no category is trusted and the email is left in the inbox
        /// for a person. This is the "route to a human" floor.
        /// </summary>
        public const double CategoryFloor = 0.35;

        /// <summary>Probability mass a category needs before its flag is set on category evidence alone.</summary>
        public const double CategoryMass = 0.50;

        /// <summary>Noul probability above which an email is treated as a sales pitch.</summary>
        public const double SalesYes = 0.75;

        /// <summary>Noul probability above which an email is treated as a recurring newsletter.</summary>
        public const double NewsletterYes = 0.75;

        /// <summary>Noul probability above which an invitation is treated as genuine.</summary>
        public const double InviteYes = 0.70;

        /// <summary>Sales probability above which an "invitation" is reclassified as a sales funnel.</summary>
        public const double InviteSalesVeto = 0.60;

        /// <summary>Noul probability required to confirm an order update relates to a real order.</summary>
        public const double OrderYes = 0.70;

        /// <summary>Noul probability above which an email is treated as a bill needing payment.</summary>
        public const double BalanceDueYes = 0.60;

        /// <summary>Noul probability above which an action request is trusted, for flag-worthy senders.</summary>
        public const double ActionYes = 0.70;

        /// <summary>Noul probability above which the recipient's personal attention is assumed.</summary>
        public const double PersonalAttentionYes = 0.70;

        /// <summary>Noul probability above which an extended summary is generated.</summary>
        public const double ExtendedSummaryYes = 0.70;

        /// <summary>Below this Score confidence a list post is never discarded as noise.</summary>
        public const double ListSubstanceFloor = 0.40;

        /// <summary>The highest substance level still treated as distribution list noise.</summary>
        public const int ListNoiseLevel = 1;

        /// <summary>
        /// Weights for the phishing signals, composed in code rather than hidden behind one
        /// "is this phishing?" question. They sum to 1.
        /// </summary>
        public const double PhishWeightDomainMismatch = 0.30;

        /// <summary>Weight of a credential or unexpected payment request in the phishing score.</summary>
        public const double PhishWeightCredentialRequest = 0.30;

        /// <summary>Weight of a mismatched link destination in the phishing score.</summary>
        public const double PhishWeightLinkMismatch = 0.20;

        /// <summary>Weight of manufactured urgency in the phishing score.</summary>
        public const double PhishWeightUrgency = 0.10;

        /// <summary>Weight of impersonal boilerplate in the phishing score.</summary>
        public const double PhishWeightBoilerplate = 0.10;

        /// <summary>Composite score at or above which an email is called phishing outright.</summary>
        public const double PhishConfirmed = 0.65;

        /// <summary>
        /// Composite score at or above which an email is suspicious but not certain. These are
        /// flagged and kept in the inbox instead of being auto-classified either way.
        /// </summary>
        public const double PhishSuspect = 0.45;

        /// <summary>Noul probability above which a caller-supplied rule is treated as matching.</summary>
        public const double RuleYes = 0.70;

        /// <summary>The number of levels in the <see cref="ListSubstance"/> rubric.</summary>
        public const int ListSubstanceLevels = 4;

        // ---------------------------------------------------------------------
        // Category option names
        // ---------------------------------------------------------------------

        /// <summary>Category: an update about a purchase the recipient already made.</summary>
        public const string CategoryOrderUpdate = "order_update";

        /// <summary>Category: a bill or invoice with money outstanding.</summary>
        public const string CategoryInvoiceDue = "invoice_due";

        /// <summary>Category: marketing, promotions or cold outreach.</summary>
        public const string CategoryPromotional = "promotional_sales";

        /// <summary>Category: a genuine invitation to attend an event.</summary>
        public const string CategoryEventInvitation = "event_invitation";

        /// <summary>Category: recurring editorial content.</summary>
        public const string CategoryNewsletter = "newsletter";

        /// <summary>Category: a post to a mailing list or distribution list thread.</summary>
        public const string CategoryListDiscussion = "list_discussion";

        /// <summary>Category: an automated notification from a social or collaboration platform.</summary>
        public const string CategorySocialNotification = "social_notification";

        /// <summary>Category: a person writing to the recipient directly.</summary>
        public const string CategoryPersonal = "personal_correspondence";

        /// <summary>Category: an account, security or service notice.</summary>
        public const string CategoryTransactional = "transactional_account";

        /// <summary>Category: none of the above.</summary>
        public const string CategoryOther = "other";

        // ---------------------------------------------------------------------
        // The questions
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets the questions asked about every email. All of them go in a single request: they are
        /// evaluated in parallel and in isolation, so the speculative ones cost only their tokens.
        /// </summary>
        public static IReadOnlyDictionary<string, Question> Standard { get; } = Build();

        private static Dictionary<string, Question> Build() => new(StringComparer.Ordinal)
        {
            [Category] = new Choice
            {
                Instructions = new
                {
                    question = "What kind of email is `email`?",
                    focus = "Classify the sender's primary purpose in writing, not the topics the email happens to mention.",
                },
                Criteria = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [CategoryOrderUpdate] = new
                    {
                        what = "Confirms, ships, delivers or receipts a purchase the recipient already made",
                        not_for = "A bill with money still owed, or an advert for something not yet bought",
                        examples = new[] { "Your order has shipped", "Thank you for your order", "Here is your receipt" },
                    },
                    [CategoryInvoiceDue] = new
                    {
                        what = "Asks the recipient to pay an amount that is outstanding",
                        not_for = "A paid receipt, or a renewal advert with no amount owed",
                        examples = new[] { "Invoice 4471, $12,840 due net 30", "Your account is past due" },
                    },
                    [CategoryPromotional] = new
                    {
                        what = "Tries to sell the recipient a product, service, course or subscription, including cold outreach and free content used as a sales funnel",
                        not_for = "Recurring editorial content the recipient subscribed to, or an invitation to a genuine event",
                        examples = new[] { "20% off this weekend", "Got 10 minutes for a quick call about growing your revenue?", "Watch this free presentation on the next big stock" },
                    },
                    [CategoryEventInvitation] = new
                    {
                        what = "Invites the recipient to attend a specific event at a stated time",
                        not_for = "A newsletter that mentions an event among its stories, or a sales pitch dressed as a webinar",
                        examples = new[] { "You are invited to our user group on 12 March", "Calendar invite: design review" },
                    },
                    [CategoryNewsletter] = new
                    {
                        what = "Recurring editorial content, a digest or a daily update from a publication the recipient subscribed to",
                        not_for = "A one-off announcement, or a promotion whose only purpose is a discount",
                        examples = new[] { "Update from the Pragmatic Engineer", "Your Monday morning briefing" },
                    },
                    [CategoryListDiscussion] = new
                    {
                        what = "A message written by a person to a mailing list or distribution list, as part of a thread",
                        not_for = "An automated posting or a broadcast announcement to the list",
                        examples = new[] { "Re: has anyone hit this plugin registration error?", "+1, we saw the same thing" },
                    },
                    [CategorySocialNotification] = new
                    {
                        what = "An automated notification from a social or collaboration platform about activity",
                        not_for = "A message a person actually wrote to the recipient",
                        examples = new[] { "You have 3 new connection requests", "Someone mentioned you in a channel" },
                    },
                    [CategoryPersonal] = new
                    {
                        what = "A person writing to the recipient directly about their own business",
                        not_for = "A bulk send that merely uses the recipient's first name",
                        examples = new[] { "Can you look at the contract before Thursday?" },
                    },
                    [CategoryTransactional] = new
                    {
                        what = "An account, security or service notice generated by something the recipient uses",
                        not_for = "Marketing from the same company",
                        examples = new[] { "Your verification code is 402118", "Your password was changed" },
                    },
                    [CategoryOther] = "An email that fits none of the other options",
                },
            },

            [SalesIntent] = new Noul
            {
                Instructions = new
                {
                    question = "Is the purpose of `email` to get the recipient to buy, subscribe to, or book a call about something?",
                    focus = "Judge the sender's commercial intent, including free content offered to open a sale.",
                },
                Criteria = new NoulCriteria
                {
                    True = new
                    {
                        what = "Promotes a purchase, subscription, consultation or paid offering, directly or through free bait content",
                        examples = new[] { "Save 30% before Friday", "Free training on how to double your revenue", "Do you have 10 minutes to chat about our platform?" },
                    },
                    False = new
                    {
                        what = "Has no commercial ask",
                        not_for = "Editorial content that happens to carry a sponsor mention while its purpose is to inform",
                        examples = new[] { "Your order shipped", "Notes from yesterday's meeting" },
                    },
                },
            },

            [RecurringNewsletter] = new Noul
            {
                Instructions = new
                {
                    question = "Is `email` an instalment of recurring editorial content that the recipient subscribed to?",
                    focus = "Look for a publication identity and multiple stories or sections, not a single announcement.",
                },
                Criteria = new NoulCriteria
                {
                    True = "Carries a publication or digest identity and delivers several stories, links or updates as its main content",
                    False = "A one-off message, an automated notification, or an advert with a single offer",
                },
            },

            [InvitesToAttend] = new Noul
            {
                Instructions = new
                {
                    question = "Does `email` invite the recipient to attend a specific event at a stated date or time?",
                    focus = "Require a real event the recipient is being asked to attend, in the sender's own message.",
                },
                Criteria = new NoulCriteria
                {
                    True = new
                    {
                        what = "Asks the recipient to attend a named event with a date, time or registration",
                        examples = new[] { "Join us live on Thursday at 2pm", "You are invited to the launch party" },
                    },
                    False = new
                    {
                        what = "No invitation to attend anything",
                        not_for = "A newsletter listing an upcoming event among its stories, a request to reply with feedback, or an invitation that only appears in quoted or forwarded text",
                        examples = new[] { "Reply and let us know what you think", "Also on our radar: next month's conference" },
                    },
                },
            },

            [OrderAlreadyPlaced] = new Noul
            {
                Instructions = "Does `email` concern a specific purchase or order the recipient has already placed?",
                Criteria = new NoulCriteria
                {
                    True = "References a particular order, booking or purchase that already exists",
                    False = "Advertises something not yet bought, or refers to no specific purchase",
                },
            },

            [BalanceDue] = new Noul
            {
                Instructions = new
                {
                    question = "Does `email` ask the recipient to pay an amount that is still outstanding?",
                    focus = "Require money currently owed, not a record of a completed payment.",
                },
                Criteria = new NoulCriteria
                {
                    True = "States an amount owed, a due date, or asks for payment to be made",
                    False = "Confirms a payment already taken, or asks for no payment at all",
                },
            },

            [RequiresRecipientAction] = new Noul
            {
                Instructions = new
                {
                    question = "Does `email` require the recipient personally to do something outside their mailbox?",
                    focus = "Require a concrete task with a real-world consequence if it is skipped.",
                },
                Criteria = new NoulCriteria
                {
                    True = new
                    {
                        what = "Names a task only the recipient can complete",
                        examples = new[] { "Your prescription is ready for pickup", "Drop the return off by Friday", "Sign and return the form" },
                    },
                    False = new
                    {
                        what = "Nothing is required of the recipient",
                        not_for = "Generic marketing calls to action such as shop now or read more",
                        examples = new[] { "Browse this week's arrivals" },
                    },
                },
            },

            [ListSubstance] = new Score
            {
                Instructions = new
                {
                    question = "How much could a reader learn from the message in `email`?",
                    inspect = "email.body_text",
                    focus = "Judge the informational content of this message only, not the thread it belongs to.",
                },
                Criteria = new object[]
                {
                    new
                    {
                        what = "Carries no information at all",
                        signals = new[] { "A bare agreement, thanks or acknowledgement", "Only an @mention or a name", "Only a link with nothing said about it" },
                    },
                    new
                    {
                        what = "States a position or a short answer with nothing to back it up",
                        signals = new[] { "An opinion with no reasoning", "A one-line answer with no detail", "Repeats the question without adding to it" },
                    },
                    new
                    {
                        what = "Answers a question or describes a problem with enough detail to be useful",
                        signals = new[] { "Names specific products, errors or versions", "Gives a workable answer a reader could act on" },
                    },
                    new
                    {
                        what = "Explains a topic, solution or decision in depth",
                        signals = new[] { "Step-by-step reasoning or instructions", "Code, configuration or references", "Covers trade-offs or edge cases" },
                    },
                },
            },

            [SenderDomainMismatch] = new Noul
            {
                Instructions = new
                {
                    question = "Does the organisation named in `email.from_name` conflict with the domain in `email.from_address`?",
                    compare = new[] { "email.from_name", "email.from_address" },
                    focus = "Compare the claimed organisation with the domain that actually sent the message.",
                },
                Criteria = new NoulCriteria
                {
                    True = new
                    {
                        what = "Claims to be an organisation that does not own the sending domain",
                        examples = new[] { "Microsoft 365 sent from account-verify.edu.py", "Acme Payroll sent from claim-bonus.example" },
                    },
                    False = new
                    {
                        what = "The claimed identity and the sending domain agree, or no organisation is claimed",
                        not_for = "A legitimate bulk sending service used by the named organisation",
                        examples = new[] { "GitHub sent from notifications.github.com" },
                    },
                },
            },

            [UrgencyPressure] = new Noul
            {
                Instructions = new
                {
                    question = "Does `email` pressure the recipient to act immediately to avoid a bad outcome?",
                    focus = "Look for manufactured consequences such as suspension, loss of access or a forfeited payment.",
                },
                Criteria = new NoulCriteria
                {
                    True = "Threatens account closure, suspension, loss of money or access unless the recipient acts at once",
                    False = "Sets no deadline, or only a commercial one such as a sale ending",
                },
            },

            [CredentialOrPaymentRequest] = new Noul
            {
                Instructions = new
                {
                    question = "Does `email` ask the recipient to disclose a credential or make an unexpected payment or transfer?",
                    focus = "Look for a request to hand over the secret itself, not an offer to reset it through a normal flow.",
                },
                Criteria = new NoulCriteria
                {
                    True = new
                    {
                        what = "Asks the recipient to reply with, enter or send a password, code, card number or bank detail, or to move money unexpectedly",
                        examples = new[] { "Reply with your password to verify", "Send the 6-digit code you just received", "Please process this wire today" },
                    },
                    False = new
                    {
                        what = "No credential or unexpected payment is requested",
                        not_for = "A normal password reset link, or an expected invoice from a known supplier",
                        examples = new[] { "Reset your password from the settings page" },
                    },
                },
            },

            [LinkDestinationMismatch] = new Noul
            {
                Instructions = new
                {
                    question = "Do the domains in `email.link_domains` conflict with the organisation named in `email.from_name`?",
                    compare = new[] { "email.link_domains", "email.from_name" },
                    focus = "Judge whether the action links plausibly belong to the organisation the email claims to be from.",
                },
                Criteria = new NoulCriteria
                {
                    True = "The links point somewhere unrelated to the claimed organisation",
                    False = "The links belong to the claimed organisation, a known service it uses, or there are no links",
                },
            },

            [ImpersonalBoilerplate] = new Noul
            {
                Instructions = new
                {
                    question = "Is `email` vague and impersonal, with no specific detail tying it to the recipient?",
                    focus = "Look for missing names, missing order or ticket numbers, and generic wording that would fit anyone.",
                },
                Criteria = new NoulCriteria
                {
                    True = "Addresses no one in particular and references nothing specific the recipient would recognise",
                    False = "Names the recipient, an account, an order, a ticket or another identifiable specific",
                },
            },

            [WarrantsExtendedSummary] = new Noul
            {
                Instructions = new
                {
                    question = "Would a one-line summary of `email` lose information the recipient would want?",
                    focus = "Judge how much distinct substance the email carries.",
                },
                Criteria = new NoulCriteria
                {
                    True = "Carries several distinct facts, stories, figures or decisions worth keeping",
                    False = "Makes a single point that fits comfortably in one line",
                },
            },

            [NeedsPersonalAttention] = new Noul
            {
                Instructions = new
                {
                    question = "Does `email` need the recipient personally to read and respond to it?",
                    focus = "Require something addressed to this recipient that another person is waiting on.",
                },
                Criteria = new NoulCriteria
                {
                    True = new
                    {
                        what = "A person is waiting on the recipient for a decision, an answer or a task",
                        examples = new[] { "Can you approve this before the end of day?" },
                    },
                    False = new
                    {
                        what = "Nothing is expected from the recipient in person",
                        not_for = "Bulk mail with a call to action aimed at every reader",
                        examples = new[] { "Your weekly digest is ready" },
                    },
                },
            },
        };

        /// <summary>
        /// Builds one yes/no question per caller-supplied rule.
        /// </summary>
        /// <param name="rules">The rules to evaluate, in order.</param>
        /// <returns>Questions keyed <c>rule_0</c>, <c>rule_1</c> and so on, matching the list order.</returns>
        /// <remarks>
        /// Rules used to be appended to the triage prompt as prose, which let them bleed into
        /// unrelated judgments. As separate questions each rule is evaluated in isolation against the
        /// same state and thresholded independently in code.
        /// </remarks>
        public static Dictionary<string, Question> ForRules(IReadOnlyList<Rules> rules)
        {
            var questions = new Dictionary<string, Question>(StringComparer.Ordinal);

            for (var index = 0; index < rules.Count; index++)
            {
                var rule = rules[index];
                if (string.IsNullOrWhiteSpace(rule?.Rule))
                    continue;

                questions[RulePrefix + index] = new Noul
                {
                    Instructions = new
                    {
                        question = "Does `email` match the rule described below?",
                        rule = rule.Rule,
                        focus = "Judge the rule literally, as written, against the email.",
                    },
                };
            }

            return questions;
        }

        /// <summary>
        /// Gets the questions used to verify a generated summary before it is returned.
        /// </summary>
        /// <remarks>
        /// Summaries come from a generative model, which can drift from the source or slip into the
        /// promotional tone the digest is meant to strip out. These checks are the guardrail pattern:
        /// cheap semantic verification on the output of an expensive model.
        /// </remarks>
        public static IReadOnlyDictionary<string, Question> SummaryChecks { get; } =
            new Dictionary<string, Question>(StringComparer.Ordinal)
            {
                ["summary_is_grounded"] = new Noul
                {
                    Instructions = new
                    {
                        question = "Is every factual claim in `summary` supported by `email`?",
                        compare = new[] { "summary", "email" },
                        focus = "Look for names, figures, dates or claims in the summary that the email does not contain.",
                    },
                },
                ["summary_is_specific"] = new Noul
                {
                    Instructions = new
                    {
                        question = "Does `summary` say what the email is actually about?",
                        focus = "A summary that only describes the kind of email it is, without its content, does not.",
                    },
                    Criteria = new NoulCriteria
                    {
                        True = "States the actual topics, figures or asks contained in the email",
                        False = "Only describes the email in general terms, such as calling it a newsletter with several stories",
                    },
                },
                ["summary_uses_promotional_tone"] = new Noul
                {
                    Instructions = new
                    {
                        question = "Does `summary` use promotional or urgency language of its own?",
                        focus = "Judge the summary's own wording, not wording it is reporting from the email.",
                    },
                    Criteria = new NoulCriteria
                    {
                        True = new
                        {
                            what = "Pushes the reader to act with marketing language",
                            examples = new[] { "Don't miss out!", "Hurry, limited time!", "Act now" },
                        },
                        False = "Reports what the email says in neutral terms",
                    },
                },
            };

        /// <summary>Noul probability above which a generated summary is rejected as ungrounded.</summary>
        public const double SummaryGroundedFloor = 0.50;

        /// <summary>Noul probability below which a generated summary is rejected as too vague.</summary>
        public const double SummarySpecificFloor = 0.40;

        /// <summary>Noul probability above which a generated summary is rejected for marketing tone.</summary>
        public const double SummaryPromotionalCeiling = 0.70;
    }
}
