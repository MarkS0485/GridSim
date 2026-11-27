"""Reference step responses for the RC5 standard dynamic-model library tests (Rc5Wp3aDynamicLibraryTests.cs, and the
WP3b cases at the end for Rc5Wp3bDynamicLibraryTests.cs).

Each case is the continuous-time model of one block diagram, integrated independently of GridSim:
linear cases by the exact LTI step response (scipy.signal.tf2ss and the matrix exponential scipy.linalg.expm), the cases with a
feedback loop by scipy.integrate.solve_ivp (Radau, rtol 1e-12, atol 1e-14). The values printed are pinned in
the C# tests with this script named as their source. Run: python tests/tooling/dynamic-library-reference.py
(NumPy and SciPy; the pinned values were produced with SciPy 1.18.1).
"""
import numpy as np
from scipy import signal
from scipy.integrate import solve_ivp
from scipy.linalg import expm

TIMES = [0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0]


def poly(*factors):
    p = np.array([1.0])
    for f in factors:
        p = np.polymul(p, np.array(f, dtype=float))
    return p


def lti_step(num, den, times):
    """Exact unit-step response of num/den at each time: the state-space form (scipy.signal.tf2ss) and the matrix
    exponential of the augmented matrix [[A, B], [0, 0]], whose top-right block is the integral of e^(A s) B."""
    a, b, c, d = signal.tf2ss(num, den)
    n = a.shape[0]
    out = []
    for t in times:
        aug = np.zeros((n + 1, n + 1))
        aug[:n, :n] = a * t
        aug[:n, n:] = b * t
        x = expm(aug)[:n, n:]
        out.append(float((c @ x + d)[0, 0]))
    return out


def show(name, values):
    print(f"// {name}")
    print("{ " + ", ".join(repr(float(v)) for v in values) + " }")


# 1. EXST1: KA (1 + s TC) / ((1 + s TB)(1 + s TA)), KA 200, TC 1, TB 10, TA 0.05; error step 0.0005 pu.
ka, tc, tb, ta, de = 200.0, 1.0, 10.0, 0.05, 0.0005
show("EXST1 dEfd", lti_step(poly([ka * de * tc, ka * de]), poly([tb, 1.0], [ta, 1.0]), TIMES))

# 2. ESDC1A (no saturation): VR = lag_{KA, TA}(e - VF), TE dEfd/dt = VR - KE Efd, VF = sKF/(1 + sTF1) Efd.
#    KA 100, TA 0.05, KE 1, TE 0.5, KF 0.05, TF1 1; from Efd0 = 1.5 (VR0 = 1.5, e0 = 0.015), e steps by 0.002.
KA, TA, KE, TE, KF, TF = 100.0, 0.05, 1.0, 0.5, 0.05, 1.0
efd0 = 1.5
e1 = efd0 * KE / KA + 0.002


def dc1a(t, x):
    vr, efd, xw = x
    vf = KF / TF * (efd - xw)
    return [(KA * (e1 - vf) - vr) / TA, (vr - KE * efd) / TE, (efd - xw) / TF]


sol = solve_ivp(dc1a, (0.0, max(TIMES)), [KE * efd0, efd0, efd0], method="Radau", rtol=1e-12, atol=1e-14, t_eval=TIMES)
show("ESDC1A Efd", sol.y[1])

# 3. ESST1A with rate feedback: u = e - VF, x = leadlag_{TC/TB}(u), VA = lag_{KA, TA}(x), Efd = VA (inside its limits),
#    VF = sKF/(1 + sTF) Efd. KA 50, TA 0.1, TC 0.5, TB 2.0, KF 0.1, TF 1.0; Efd0 = 2.0, e steps by 0.004 pu.
KA3, TA3, TC3, TB3, KF3, TF3 = 50.0, 0.1, 0.5, 2.0, 0.1, 1.0
e3 = 2.0 / KA3 + 0.004


def st1a(t, x):
    xl, va, xw = x            # lead-lag internal lag state (output = c u + xl), regulator, washout state
    efd = va
    vf = KF3 / TF3 * (efd - xw)
    u = e3 - vf
    c = TC3 / TB3
    xo = c * u + xl
    return [((1.0 - c) * u - xl) / TB3, (KA3 * xo - va) / TA3, (efd - xw) / TF3]


u0 = 2.0 / KA3
sol3 = solve_ivp(st1a, (0.0, max(TIMES)), [(1.0 - TC3 / TB3) * u0, 2.0, 2.0], method="Radau", rtol=1e-12, atol=1e-14, t_eval=TIMES)
show("ESST1A Efd", sol3.y[1])

# 4. IEEEST on speed: (1/(1 + A1 s)) (1 + T1 s)/(1 + T2 s) (1 + T3 s)/(1 + T4 s) KS T5 s/(1 + T6 s); speed step 0.001 pu.
A1, T1, T2, T3, T4, T5, T6, KS = 0.05, 0.2, 0.05, 0.3, 0.1, 1.5, 2.0, 10.0
dw = 0.001
show("IEEEST Vs", lti_step(poly([KS * T5 * dw, 0.0], [T1, 1.0], [T3, 1.0]), poly([A1, 1.0], [T2, 1.0], [T4, 1.0], [T6, 1.0]), TIMES))

# 5. PSS2A, speed on input 1 (step 0.001 pu), power on input 2 held: KS1 R(s) W1 W2 / (1 + s T6) LL1 LL2,
#    R(s) = (1 + s T8) / (1 + s T9)^M (N = 1). TW1 = TW2 = 2, T6 = 0.02, T8 = 0.5, T9 = 0.1, M = 5, KS1 = 10,
#    T1 = 0.15, T2 = 0.03, T3 = 0.15, T4 = 0.03.
TW, T6b, T8, T9, M, KS1, L1, L2 = 2.0, 0.02, 0.5, 0.1, 5, 10.0, 0.15, 0.03
num = poly([KS1 * dw * TW * TW, 0.0, 0.0], [T8, 1.0], [L1, 1.0], [L1, 1.0])
den = poly([TW, 1.0], [TW, 1.0], [T6b, 1.0], *([[T9, 1.0]] * M), [L2, 1.0], [L2, 1.0])
show("PSS2A Vs (speed step)", lti_step(num, den, TIMES))

# 6. PSS2A, electrical power on input 2 (step 0.1 pu), speed held: n2 = KS2 W3 / (1 + s T7) dP (TW4 = 0 bypassed),
#    y = KS1 (KS3 R(s) - 1) n2, then the two lead-lags. KS2 = 0.4, KS3 = 1, T7 = 2, TW3 = 2.
KS2, KS3, T7, TW3, dp = 0.4, 1.0, 2.0, 2.0, 0.1
rnum, rden = np.array([T8, 1.0]), poly(*([[T9, 1.0]] * M))
mix_num = np.polysub(KS3 * rnum, rden)          # KS3 R - 1 = (KS3 rnum - rden) / rden
num = poly(KS1 * KS2 * dp * mix_num, [TW3, 0.0], [L1, 1.0], [L1, 1.0])
den = poly(rden, [TW3, 1.0], [T7, 1.0], [L2, 1.0], [L2, 1.0])
show("PSS2A Vs (power step)", lti_step(num, den, TIMES))


# ------------------------------------------------------------------------------------------------------------------------
# RC5 WP3b (Rc5Wp3bDynamicLibraryTests.cs): governors and dynamic loads. Each is the continuous-time model, written here
# from its block diagram or equations (not from GridSim's code), integrated by solve_ivp (Radau, rtol 1e-12, atol 1e-14).
from scipy.optimize import brentq, fsolve

GOV_TIMES = [0.5, 1.0, 2.0, 5.0, 10.0, 20.0]

# 7. GGOV1, Rselect 1, Flag 0, closed on its own power (Pelec = Pmech, the machine at steady state), speed step -0.002 pu.
#    R 0.04, Tpelec 1, Kpgov 10, Kigov 2, Kdgov 0, Tact 0.5, Kturb 1.5, Wfnl 0.2, Tb 0.1, Tc 0, Dm 0; Pm0 = 0.6. No limit
#    binds (the load limiter's output stays above 1 > fsr; the acceleration limiter is off with Ka = 0).
R, TPE, KP, KI, TACT, KT, WFNL, TB = 0.04, 1.0, 10.0, 2.0, 0.5, 1.5, 0.2, 0.1
PM0, DW = 0.6, -0.002
FSR0 = PM0 / KT + WFNL
PREF = R * PM0


def ggov1(t, x):
    pef, xi, valve, pm = x
    err = PREF - R * pef - DW
    fsrn = KP * err + xi
    return [(pm - pef) / TPE, KI * err, (fsrn - valve) / TACT, (KT * (valve - WFNL) - pm) / TB]


sol = solve_ivp(ggov1, (0.0, max(GOV_TIMES)), [PM0, FSR0, FSR0, PM0], method="Radau", rtol=1e-12, atol=1e-14, t_eval=GOV_TIMES)
show("GGOV1 Pmech", sol.y[3])

# 8. WSIEG1 with a nonlinear gate-to-flow table, speed step -0.004 pu: K 20, T1 0.2, T2 0.1, T3 0.3, Uo 1, Uc -1,
#    T4 0.3, T5 7, T6 0.5, T7 0.05, K1 0.3, K3 0.4, K5 0.3; table (0, 0), (0.3, 0.4), (0.6, 0.75), (0.8, 0.9), (1, 1); Pm0 0.7.
KW, T1W, T2W, T3W, T4W, T5W, T6W, T7W = 20.0, 0.2, 0.1, 0.3, 0.3, 7.0, 0.5, 0.05
K1W, K3W, K5W = 0.3, 0.4, 0.3
GV, PGV = [0.0, 0.3, 0.6, 0.8, 1.0], [0.0, 0.4, 0.75, 0.9, 1.0]
PMW0, DWW = 0.7, -0.004
GATE0 = float(np.interp(PMW0, PGV, GV))


def wsieg1(t, x):
    xl, gate, y4, y5, y6, y7 = x
    e = -KW * DWW
    c = T2W / T1W
    s1 = c * e + xl
    rate = min(max((GATE0 + s1 - gate) / T3W, -1.0), 1.0)
    pgv = float(np.interp(gate, GV, PGV))
    return [((1.0 - c) * e - xl) / T1W, rate, (pgv - y4) / T4W, (y4 - y5) / T5W, (y5 - y6) / T6W, (y6 - y7) / T7W]


solw = solve_ivp(wsieg1, (0.0, max(GOV_TIMES)), [0.0, GATE0, PMW0, PMW0, PMW0, PMW0], method="Radau", rtol=1e-12, atol=1e-14,
                 t_eval=GOV_TIMES)
show("WSIEG1 Pmech", K1W * solw.y[2] + K3W * solw.y[3] + K5W * solw.y[4])

# 9. Third-order induction motor from its equivalent circuit (Overbye's CIM5 example: Ra 0.01, Xa 0.06, Xm 4, R1 0.03,
#    X1 0.04 on the motor base), H 1, 50 Hz, load torque T0 w^2; drawing 0.8 pu at 1 pu, then the voltage steps to 0.9 pu.
RA, XA, XM, R1, X1, HM, FHZ, DEXP = 0.01, 0.06, 4.0, 0.03, 0.04, 1.0, 50.0, 2.0
WS = 2.0 * np.pi * FHZ
XS, XP, TPO = XA + XM, XA + XM * X1 / (XM + X1), (X1 + XM) / (WS * R1)


def z_circuit(s):
    rotor = R1 / s + 1j * X1
    return RA + 1j * XA + (1j * XM * rotor) / (1j * XM + rotor)


s0 = brentq(lambda s: (1.0 / z_circuit(s)).real - 0.8, 1e-9, 0.05, xtol=1e-16, rtol=1e-15)
i0 = 1.0 / z_circuit(s0)
e0 = 1.0 - (RA + 1j * XP) * i0
T0 = (e0 * np.conj(i0)).real
W0 = 1.0 - s0
V1 = 0.9


def motor(t, x):
    e = x[0] + 1j * x[1]
    w = x[2]
    i = (V1 - e) / (RA + 1j * XP)
    de = -(e - 1j * (XS - XP) * i) / TPO - 1j * (1.0 - w) * WS * e
    te = (e * np.conj(i)).real
    return [de.real, de.imag, (te - T0 * (w / W0) ** DEXP) / (2.0 * HM)]


solm = solve_ivp(motor, (0.0, max(TIMES)), [e0.real, e0.imag, W0], method="Radau", rtol=1e-12, atol=1e-14, t_eval=TIMES)
show("motor speed", solm.y[2])
show("motor P", [((V1 * np.conj((V1 - (a + 1j * b)) / (RA + 1j * XP))).real) for a, b in zip(solm.y[0], solm.y[1])])

# 10. ZIP with exponential recovery, P side: P0 1, fr 0.5, as 0, at 2, Tp 2, static Z 0.3, I 0.3 (the rest constant power);
#     the voltage steps from 1 to 0.9 pu.
FR, AS, AT, TP, ZP, IP, U = 0.5, 0.0, 2.0, 2.0, 0.3, 0.3, 0.9


def zipr(t, x):
    return [(-x[0] + FR * (U ** AS - U ** AT)) / TP]


solz = solve_ivp(zipr, (0.0, max(TIMES)), [0.0], method="Radau", rtol=1e-12, atol=1e-14, t_eval=TIMES)
show("ZIP recovery P", [(1.0 - FR) * (ZP * U * U + IP * U + (1.0 - ZP - IP)) + xp + FR * U ** AT for xp in solz.y[0]])

# 11. CMLD subset on a 100 MVA system base: the bus at 1 pu angle 0 draws 0.8 + j0.3 pu; load MVA base 100; transformer
#     X 0.08, tap 1.0; substation shunt B 0.02; feeder 0.02 + j0.04 with compensation B 0.04, Fb 0.5; motor A (the motor
#     of case 9, its torque T0 w^2) takes 40 % of the far-end power at loading factor 0.8; the static load P1c 0.5 P1e 2,
#     P2c 0.3 P2e 1 for P and Q, power factor 0.95; the bus voltage then steps to 0.9 pu.
PH, QH, XT, BSS, ZF, BF, FB, FMA, LF, PFS = 0.8, 0.3, 0.08, 0.02, 0.02 + 0.04j, 0.04, 0.5, 0.4, 0.8, 0.95
TANP = np.sqrt(1 - PFS ** 2) / PFS


def static_factor(u):
    return 0.5 * u * u + 0.3 * u + 0.2


def motor_init(vf, pmotor_pu):
    sm = brentq(lambda s: abs(vf) ** 2 * (1.0 / z_circuit(s)).real - pmotor_pu, 1e-12, 0.2, xtol=1e-16, rtol=1e-15)
    im = vf / z_circuit(sm)
    return sm, im, vf - (RA + 1j * XP) * im


def cmld_back(vf, pend):
    k = FMA * pend / LF
    sm, im, em = motor_init(vf, LF)
    ps = (1 - FMA) * pend
    i_f = 1j * (1 - FB) * BF * vf + im * k + np.conj((ps + 1j * ps * TANP) / vf)
    vl = vf + ZF * i_f
    it = 1j * (BSS + FB * BF) * vl + i_f
    vh = vl + 1j * XT * it
    return vh, it, sm, em, k, ps


def resid(x):
    vh, it, *_ = cmld_back(x[0] + 1j * x[1], x[2])
    return [vh.real - 1.0, vh.imag, (vh * np.conj(it)).real - PH]


xs = fsolve(resid, [0.95, -0.05, 0.8], xtol=1e-15)
VF0 = xs[0] + 1j * xs[1]
_, _, SM0, EM0, KM, PS0 = cmld_back(VF0, xs[2])
TM0 = (EM0 * np.conj((VF0 - EM0) / (RA + 1j * XP))).real
WM0 = 1.0 - SM0
YM = KM / (RA + 1j * XP)


def far_end(vh, em):
    """The far-end voltage for bus voltage vh and motor EMF em: the two internal nodes solved with the static load as a
    current at the far-end voltage, by fsolve on the real and imaginary parts."""
    def f(x):
        vf = x[0] + 1j * x[1]
        vl = x[2] + 1j * x[3]
        u = abs(vf) / abs(VF0)
        sst = PS0 * static_factor(u) * (1 + 1j * TANP)
        i_f = 1j * (1 - FB) * BF * vf + YM * (vf - em) + np.conj(sst / vf)
        it = (vh - vl) / (1j * XT)
        e1 = (vl - vf) - ZF * i_f
        e2 = it - 1j * (BSS + FB * BF) * vl - i_f
        return [e1.real, e1.imag, e2.real, e2.imag]
    x = fsolve(f, [VF0.real, VF0.imag, VF0.real, VF0.imag], xtol=1e-15)
    return x[0] + 1j * x[1], x[2] + 1j * x[3]


def cmld(t, x):
    em = x[0] + 1j * x[1]
    w = x[2]
    vf, _ = far_end(V1, em)
    i = (vf - em) / (RA + 1j * XP)
    de = -(em - 1j * (XS - XP) * i) / TPO - 1j * (1.0 - w) * WS * em
    te = (em * np.conj(i)).real
    return [de.real, de.imag, (te - TM0 * (w / WM0) ** DEXP) / (2.0 * HM)]


solc = solve_ivp(cmld, (0.0, max(TIMES)), [EM0.real, EM0.imag, WM0], method="Radau", rtol=1e-12, atol=1e-14, t_eval=TIMES)
show("CMLD motor A speed", solc.y[2])


# ------------------------------------------------------------------------------------------------------------------------
# RC5 WP19 (Rc5Wp19ControllerLibraryTests.cs): EXDC2/IEEEX1, ESAC8B, ESST3A, PSS2C and IEEEG3. Each is the continuous-time
# model written from its block diagram (IEEE 421.5 / the PSS/E data sheet), not from GridSim's code.

# 15. ESAC8B, the spec's two-lag case: KP 10, KI = KD = 0, KA 1, TA 0.1, KE 1, TE 0.5, KC 0, SE 0; error step 0.01. Closed
#     form Efd - Efd0 = 0.1 [1 - (TA e^(-t/TA) - TE e^(-t/TE)) / (TA - TE)], and the discrete chain GridSim steps (VR by the
#     exact hold of its lag, then VE by the exact hold of its pole driven by the NEW VR) run by scipy.signal.dlsim at 1 ms.
TA8, TE8, DT8 = 0.1, 0.5, 1e-3
T8S = [0.05, 0.1, 0.2, 0.5, 1.0, 2.0]
show("ESAC8B closed form dEfd", [0.1 * (1.0 - (TA8 * np.exp(-t / TA8) - TE8 * np.exp(-t / TE8)) / (TA8 - TE8)) for t in T8S])
a8, b8 = np.exp(-DT8 / TA8), np.exp(-DT8 / TE8)
A8 = np.array([[a8, 0.0], [(1.0 - b8) * a8, b8]])
B8 = np.array([[1.0 - a8], [(1.0 - b8) * (1.0 - a8)]])
n8 = int(round(max(T8S) / DT8))
_, y8, _ = signal.dlsim((A8, B8, np.array([[0.0, 1.0]]), np.array([[0.0]]), DT8), np.full(n8 + 1, 0.1), x0=[0.0, 0.0])
# dlsim's output k is the state after k inputs; GridSim's step k (1-based) returns the state after k held inputs.
show("ESAC8B discrete chain dEfd at 1 ms", [float(y8[int(round(t / DT8)), 0]) for t in T8S])

# 16. ESAC8B with the full PID: KP 5, KI 2, KD 0.5, TD 0.05, KA 2, TA 0.05, KE 1, TE 0.8; error step 0.002 from rest.
KP8, KI8, KD8, TD8, KA8b, TA8b, KE8, TE8b, E8 = 5.0, 2.0, 0.5, 0.05, 2.0, 0.05, 1.0, 0.8, 0.002
# PID (KP + KI/s + KD s/(1 + TD s)) = (KP s (1 + TD s) + KI (1 + TD s) + KD s^2) / (s (1 + TD s))
pid_num = np.polyadd(np.polyadd(poly([KP8, 0.0], [TD8, 1.0]), poly([KI8], [TD8, 1.0])), [KD8, 0.0, 0.0])
show("ESAC8B PID dEfd", lti_step(poly(E8 * KA8b * pid_num), poly([1.0, 0.0], [TD8, 1.0], [TA8b, 1.0], [TE8b, KE8]), TIMES[:5]))

# 17. IEEEG3 closed loop, speed step +0.001 pu at rest from g0 = 0.5: TG 0.2, TP 0.04, sigma 0.05, delta 0.3, TR 5, TW 1,
#     classical a-values. No limit binds (|v| stays far below Uo, Uc; the gate inside its limits).
TG, TP, SIG, DEL, TRG, TW3, G0, DWG = 0.2, 0.04, 0.05, 0.3, 5.0, 1.0, 0.5, 0.001
A11, A13, A21, A23 = 0.5, 1.0, 1.5, 1.0
P0 = SIG * G0


def ieeeg3(t, x):
    v, g, xd, xt = x          # gate velocity, gate, dashpot lag state (washout output g - xd), turbine lag state
    err = P0 - DWG - SIG * g - DEL * (g - xd)
    c = (A11 * A23 - A13 * A21) / A11
    return [(err / TG - v) / TP, v, (g - xd) / TRG, ((A23 - c) * g - xt) / (A11 * TW3)]


c3 = (A11 * A23 - A13 * A21) / A11
GT = [0.5, 1.0, 2.0, 5.0, 10.0]
solg = solve_ivp(ieeeg3, (0.0, max(GT)), [0.0, G0, G0, (A23 - c3) * G0], method="Radau", rtol=1e-12, atol=1e-14, t_eval=GT)
show("IEEEG3 Pm", c3 * solg.y[1] + solg.y[3])

# 18. ESST3A, KC 0 and VB held (fixed terminal phasors): KA 20, TA 0.02, TC 1, TB 5, KM 8, TM 0.4, KG 1, VB 5; the inner
#     loop TM dVM/dt = KM (VR - KG VB VM) - VM. From Efd0 = 2 (VM0 0.4, VR0 = 0.4/8 + 2 = 2.05, x0 = 0.1025), the error
#     steps by 0.001.
KA3a, TA3a, TC3a, TB3a, KM3, TM3, KG3, VB3 = 20.0, 0.02, 1.0, 5.0, 8.0, 0.4, 1.0, 5.0
VM30 = 2.0 / VB3
VR30 = VM30 / KM3 + KG3 * 2.0
X30 = VR30 / KA3a


def st3a(t, x):
    xl, vr, vm = x
    u = X30 + 0.001
    c = TC3a / TB3a
    xo = c * u + xl
    return [((1.0 - c) * u - xl) / TB3a, (KA3a * xo - vr) / TA3a, (KM3 * (vr - KG3 * VB3 * vm) - vm) / TM3]


sol3a = solve_ivp(st3a, (0.0, max(TIMES)), [(1.0 - TC3a / TB3a) * X30, VR30, VM30], method="Radau", rtol=1e-12, atol=1e-14, t_eval=TIMES)
show("ESST3A Efd", VB3 * sol3a.y[2])

# 19. PSS2C on speed (input 2 electrical power held, so n2 = 0 after the first step), M = N = 0: KS1 W1 W2 / (1 + s T6)
#     LL1 LL2 LL3 LL4. TW1 = TW2 = 2, T6 0.02, KS1 10, T1 0.15/T2 0.03, T3 0.15/T4 0.03, T10 0.1/T11 0.05, T12 0.2/T13 0.04.
num = poly([KS1 * dw * TW * TW, 0.0, 0.0], [0.15, 1.0], [0.15, 1.0], [0.1, 1.0], [0.2, 1.0])
den = poly([TW, 1.0], [TW, 1.0], [0.02, 1.0], [0.03, 1.0], [0.03, 1.0], [0.05, 1.0], [0.04, 1.0])
show("PSS2C Vs (speed step)", lti_step(num, den, TIMES))

# ------------------------------------------------------------------------------------------------------------------------
# RC5 WP20 (Rc5Wp20CompositeLoadTests.cs): the composite load with its WP20 parts, written from the WECC Composite Load
# Model Specification (motor D, electronic load) and the circuits of case 11, not from GridSim's code.
#
# 20. The case-11 network (bus at 1 pu draws 0.8 + j0.3 pu; transformer X 0.08, tap 1; Bss 0.02; feeder 0.02 + j0.04, B 0.04,
#     Fb 0.5) with motor A (the case-9 motor, torque T0 w^2, LF 0.8) at 30 %, motor B (the same circuit, H 0.5, constant
#     torque, LF 0.7) at 20 %, motor D (WECC run curve: Po = LF 0.8, Q'o = Po tan(acos 0.97) - 6 (1 - 0.86)^2,
#     Q = Q'o + 6 (V - 0.86)^2 above Vbrk 0.86) at 15 %, the electronic load (PF 0.95, constant power above Vd1 0.7) at 10 %,
#     the static load of case 11 for the rest (25 %); the bus voltage then steps to 0.95 pu (every far-end voltage stays
#     above Vbrk and Vd1, and no relay is set).
FMA20, FMB20, FMD20, FEL20 = 0.3, 0.2, 0.15, 0.1
FST20 = 1.0 - FMA20 - FMB20 - FMD20 - FEL20
LFA20, LFB20, LFD20, HB20 = 0.8, 0.7, 0.8, 0.5
TAND20, TANE20 = np.sqrt(1 - 0.97 ** 2) / 0.97, np.sqrt(1 - 0.95 ** 2) / 0.95
QOD20 = LFD20 * TAND20 - 6.0 * (1.0 - 0.86) ** 2
V20 = 0.95


def motor_d_q(v):
    return QOD20 + 6.0 * (v - 0.86) ** 2


def cmld20_back(vf, pend):
    ka, kb = FMA20 * pend / LFA20, FMB20 * pend / LFB20
    sa, ia, ea = motor_init(vf, LFA20)
    sb, ib, eb = motor_init(vf, LFB20)
    pd = FMD20 * pend
    qd = pd / LFD20 * motor_d_q(abs(vf))
    pe = FEL20 * pend
    ps = FST20 * pend
    i_f = (1j * (1 - FB) * BF * vf + ia * ka + ib * kb + np.conj((pd + 1j * qd) / vf) + np.conj((pe + 1j * pe * TANE20) / vf)
           + np.conj((ps + 1j * ps * TANP) / vf))
    vl = vf + ZF * i_f
    it = 1j * (BSS + FB * BF) * vl + i_f
    vh = vl + 1j * XT * it
    return vh, it, (sa, ea, ka), (sb, eb, kb), pd, pe, ps


def resid20(x):
    vh, it, *_ = cmld20_back(x[0] + 1j * x[1], x[2])
    return [vh.real - 1.0, vh.imag, (vh * np.conj(it)).real - PH]


x20 = fsolve(resid20, [0.95, -0.05, 0.8], xtol=1e-15)
VF20 = x20[0] + 1j * x20[1]
_, _, (SA20, EA20, KA20), (SB20, EB20, KB20), PD20, PE20, PS20 = cmld20_back(VF20, x20[2])
YA20, YB20 = KA20 / (RA + 1j * XP), KB20 / (RA + 1j * XP)
TA20 = (EA20 * np.conj((VF20 - EA20) / (RA + 1j * XP))).real
TB20 = (EB20 * np.conj((VF20 - EB20) / (RA + 1j * XP))).real
QE20 = PE20 * TANE20
BASED20 = PD20 / LFD20


def far_end20(vh, ea, eb):
    def f(x):
        vf = x[0] + 1j * x[1]
        vl = x[2] + 1j * x[3]
        v = abs(vf)
        u = v / abs(VF20)
        sst = PS20 * static_factor(u) * (1 + 1j * TANP)
        sd = BASED20 * (LFD20 + 1j * motor_d_q(v))
        i_f = (1j * (1 - FB) * BF * vf + YA20 * (vf - ea) + YB20 * (vf - eb) + np.conj(sd / vf) + np.conj((PE20 + 1j * QE20) / vf)
               + np.conj(sst / vf))
        it = (vh - vl) / (1j * XT)
        e1 = (vl - vf) - ZF * i_f
        e2 = it - 1j * (BSS + FB * BF) * vl - i_f
        return [e1.real, e1.imag, e2.real, e2.imag]
    x = fsolve(f, [VF20.real, VF20.imag, VF20.real, VF20.imag], xtol=1e-15)
    return x[0] + 1j * x[1]


def cmld20(t, x):
    ea, eb = x[0] + 1j * x[1], x[3] + 1j * x[4]
    wa, wb = x[2], x[5]
    vf = far_end20(V20, ea, eb)
    out = []
    for e, w, t0, w0, h, dexp in ((ea, wa, TA20, 1.0 - SA20, HM, DEXP), (eb, wb, TB20, 1.0 - SB20, HB20, 0.0)):
        i = (vf - e) / (RA + 1j * XP)
        de = -(e - 1j * (XS - XP) * i) / TPO - 1j * (1.0 - w) * WS * e
        te = (e * np.conj(i)).real
        out += [de.real, de.imag, (te - t0 * (w / w0) ** dexp) / (2.0 * h)]
    return out


sol20 = solve_ivp(cmld20, (0.0, max(TIMES)), [EA20.real, EA20.imag, 1.0 - SA20, EB20.real, EB20.imag, 1.0 - SB20],
                  method="Radau", rtol=1e-12, atol=1e-14, t_eval=TIMES)
show("WP20 CMLD motor A speed", sol20.y[2])
show("WP20 CMLD motor B speed", sol20.y[5])

# 20b. Case 20 at more sample times (every 0.01 s to 0.3 s, then 0.5, 1, 2 and 5 s), for the per-time check in
#      Rc5Wp20PartBTests.
TIMES20B = [round(0.01 * k, 2) for k in range(1, 31)] + [0.5, 1.0, 2.0, 5.0]
sol20b = solve_ivp(cmld20, (0.0, max(TIMES20B)), [EA20.real, EA20.imag, 1.0 - SA20, EB20.real, EB20.imag, 1.0 - SB20],
                   method="Radau", rtol=1e-12, atol=1e-14, t_eval=TIMES20B)
show("WP20 CMLD motor A speed, dense", sol20b.y[2])
show("WP20 CMLD motor B speed, dense", sol20b.y[5])