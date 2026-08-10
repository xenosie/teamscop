using System.Text.Json;

namespace Teamscop.Engine.Lifecycle;

public enum ApprovalPurpose
{
    Usb = 0,
    Uninstall = 1
}

/// <summary>Why a code was refused, so the caller can say something useful without leaking timing.</summary>
public enum ApprovalRefusal
{
    None = 0,
    NoSecret,
    Malformed,
    Invalid,
    AlreadyUsed,
    LockedOut
}

public sealed record ApprovalCheck(bool Ok, ApprovalRefusal Refusal, long MatchedStep)
{
    public static ApprovalCheck Allow(long step) => new(true, ApprovalRefusal.None, step);

    public static ApprovalCheck Deny(ApprovalRefusal refusal) => new(false, refusal, -1);

    public string Describe() => Refusal switch
    {
        ApprovalRefusal.NoSecret => "This machine is not enrolled yet.",
        ApprovalRefusal.Malformed => "Enter the 6-digit code.",
        ApprovalRefusal.AlreadyUsed => "That code was already used. Ask for a fresh one.",
        ApprovalRefusal.LockedOut => "Too many wrong codes. Try again in a few minutes.",
        // The likeliest cause by far, and the one the bare "Incorrect code." hid: USB and uninstall
        // codes are derived separately, so the USB one is refused here every time. Naming that turns
        // an apparently broken feature into an obvious mistake.
        _ => "Incorrect code. Make sure it is the UNINSTALL code, not the USB unlock code."
    };
}

public interface IApprovalCodeVerifier
{
    /// <summary>False only when there is no device key to derive from — the caller must fail closed.</summary>
    bool HasSecret(ApprovalPurpose purpose);

    ApprovalCheck Verify(ApprovalPurpose purpose, string? code);
}

/// <summary>
/// §6 / §9.6 / §11.2 — verifies a 6-digit approval code entirely on this machine, with no stored
/// secret and no enrolment. The secret is DERIVED on demand from the machine's device key via the
/// single shared <see cref="TotpGenerator.DeriveMachineSecret"/> (§6.1/§6.2), so the server (from
/// <c>users.DeviceKey</c>) and the agent compute the identical secret and codes verify offline
/// (§6.4). There is no more "no secret yet / connect once" state — a machine with a device key can
/// approve from first boot.
///
/// §6.3 — the time step is company-local: <see cref="_companyNow"/> supplies
/// <c>new DateTimeOffset(businessClock.Now().BusinessLocal, TimeSpan.Zero)</c> at the construction
/// site, mirroring the server. §6.5 lives in <see cref="TotpGenerator.DeriveMachineSecret"/>: adding
/// the company key to the derivation input is a one-line change there and nowhere else.
/// </summary>
public sealed class LocalApprovalVerifier : IApprovalCodeVerifier
{
    /// <summary>±1 step, i.e. the code stays valid for the 30 s either side of the admin reading it out.</summary>
    private const int StepWindow = 1;

    private const int MaxFailures = 8;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<string?> _deviceKey;
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _companyNow;
    private readonly object _gate = new();

    public LocalApprovalVerifier(
        Func<string?> deviceKey,
        string stateDirectory,
        Func<DateTimeOffset>? companyNow = null)
    {
        _deviceKey = deviceKey;
        Directory.CreateDirectory(stateDirectory);
        _statePath = Path.Combine(stateDirectory, "approval-state.json");
        // Defaults to UTC so tests and a UTC-zone company behave identically; production wires the
        // company-local instant so agent and server derive the same §6.3 step.
        _companyNow = companyNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool HasSecret(ApprovalPurpose purpose)
        => !string.IsNullOrWhiteSpace(_deviceKey());

    public ApprovalCheck Verify(ApprovalPurpose purpose, string? code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Length != TotpGenerator.Digits
            || !trimmed.All(char.IsAsciiDigit))
        {
            return ApprovalCheck.Deny(ApprovalRefusal.Malformed);
        }

        var deviceKey = _deviceKey();
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return ApprovalCheck.Deny(ApprovalRefusal.NoSecret);
        }

        // §6.1 — derived on demand from the device key alone, the same function the server uses.
        var secret = TotpGenerator.DeriveMachineSecret(deviceKey, PurposeName(purpose));

        lock (_gate)
        {
            var now = _companyNow();
            var state = LoadState();

            // The lockout is PER PURPOSE. USB and uninstall codes are verified against different
            // derivations, sometimes by different processes, and a shared counter meant eight wrong
            // USB attempts blocked a perfectly correct uninstall code for fifteen minutes — which in
            // the field read as "uninstall is broken", not as rate limiting.
            //
            // B15 — the counter must decay, or an attacker primes it to 7 and the next genuine
            // typo locks the employee out. It resets on success AND when the window has passed.
            var (failures, lastFailure, lockedUntil) = GetCounters(state, purpose);
            if (lockedUntil is { } until)
            {
                if (now < until)
                {
                    return ApprovalCheck.Deny(ApprovalRefusal.LockedOut);
                }

                lockedUntil = null;
                failures = 0;
            }
            else if (lastFailure is { } last && now - last > LockoutDuration)
            {
                failures = 0;
            }

            if (!TotpGenerator.VerifyCode(secret, trimmed, StepWindow, now, out var matchedStep))
            {
                failures++;
                lastFailure = now;
                if (failures >= MaxFailures)
                {
                    lockedUntil = now + LockoutDuration;
                }

                SetCounters(state, purpose, failures, lastFailure, lockedUntil);
                SaveState(state);
                return ApprovalCheck.Deny(ApprovalRefusal.Invalid);
            }

            // Replay defence: the admin reads one code out of band; it opens one thing, once.
            // Not counted as a failure — it is a repeat of something that was legitimate.
            if (matchedStep <= LastUsedStep(state, purpose))
            {
                return ApprovalCheck.Deny(ApprovalRefusal.AlreadyUsed);
            }

            SetLastUsedStep(state, purpose, matchedStep);
            SetCounters(state, purpose, failures: 0, lastFailure: null, lockedUntil: null);
            SaveState(state);
            return ApprovalCheck.Allow(matchedStep);
        }
    }

    /// <summary>The legacy shared fields carry the USB counters, so old state files stay meaningful.</summary>
    private static (int Failures, DateTimeOffset? LastFailure, DateTimeOffset? LockedUntil) GetCounters(
        VerifierState state, ApprovalPurpose purpose)
        => purpose == ApprovalPurpose.Usb
            ? (state.Failures, state.LastFailureAtUtc, state.LockedUntilUtc)
            : (state.FailuresUninstall, state.LastFailureUninstallAtUtc, state.LockedUntilUninstallUtc);

    private static void SetCounters(
        VerifierState state,
        ApprovalPurpose purpose,
        int failures,
        DateTimeOffset? lastFailure,
        DateTimeOffset? lockedUntil)
    {
        if (purpose == ApprovalPurpose.Usb)
        {
            state.Failures = failures;
            state.LastFailureAtUtc = lastFailure;
            state.LockedUntilUtc = lockedUntil;
        }
        else
        {
            state.FailuresUninstall = failures;
            state.LastFailureUninstallAtUtc = lastFailure;
            state.LockedUntilUninstallUtc = lockedUntil;
        }
    }

    private static string PurposeName(ApprovalPurpose purpose)
        => purpose == ApprovalPurpose.Usb ? TotpGenerator.PurposeUsb : TotpGenerator.PurposeUninstall;

    private static long LastUsedStep(VerifierState state, ApprovalPurpose purpose)
        => purpose == ApprovalPurpose.Usb ? state.LastUsedStepUsb : state.LastUsedStepUninstall;

    private static void SetLastUsedStep(VerifierState state, ApprovalPurpose purpose, long step)
    {
        if (purpose == ApprovalPurpose.Usb)
        {
            state.LastUsedStepUsb = step;
        }
        else
        {
            state.LastUsedStepUninstall = step;
        }
    }

    private VerifierState LoadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<VerifierState>(File.ReadAllText(_statePath), JsonOptions) ?? new VerifierState()
                : new VerifierState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new VerifierState();
        }
    }

    private void SaveState(VerifierState state)
    {
        try
        {
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the counter fails open on rate limiting only; the code itself still has to be right.
        }
    }

    private sealed class VerifierState
    {
        // The unsuffixed trio is the USB purpose's counters — the names predate the split and old
        // state files on disk still carry them, so renaming would silently reset live lockouts.
        public int Failures { get; set; }
        public DateTimeOffset? LastFailureAtUtc { get; set; }
        public DateTimeOffset? LockedUntilUtc { get; set; }
        public int FailuresUninstall { get; set; }
        public DateTimeOffset? LastFailureUninstallAtUtc { get; set; }
        public DateTimeOffset? LockedUntilUninstallUtc { get; set; }
        public long LastUsedStepUsb { get; set; } = -1;
        public long LastUsedStepUninstall { get; set; } = -1;
    }
}
