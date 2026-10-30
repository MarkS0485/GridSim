function mpc = cigre_mv
%CIGRE_MV  CIGRE Task Force C6.04.02 European medium-voltage distribution benchmark.
%   From the final report "Benchmark Systems for Network Integration of Renewable
%   and Distributed Energy Resources", CIGRE TF C6.04.02, 2014.
%
%   15-node radial MV feeder: one 110 kV source (Bus 0) feeding two 20 kV feeders
%   through two 25 MVA 110/20 kV transformers. Base (no-DER) configuration with the
%   three normally-open tie switches S1 (14-8), S2 (6-7) and S3 (11-4) left open, so
%   the network is a tree. Line/transformer/load data reproduce the pandapower
%   reference implementation (create_cigre_network_mv, e2nIEE/pandapower).
%
%   Per-unit conversion: 100 MVA system base; at 20 kV, Zbase = 20^2/100 = 4.0 ohm.
%   R_pu = R_ohm/Zbase, X_pu = X_ohm/Zbase, B_pu = 2*pi*50 * C * Zbase (total charging).
%   Cables (feeder 1): r=0.501, x=0.716 ohm/km, c=151.1749 nF/km.
%   Overhead (feeder 2, lines 12-13, 13-14): r=0.510, x=0.366 ohm/km, c=10.09679 nF/km.
%   Transformers: vk=12.00107%, vkr=0.16% on 25 MVA -> R=0.0064, X=0.48 pu on 100 MVA,
%   Dyn 30 deg phase shift.
%
%   NOTE: this is a radial distribution feeder, not a transmission system; the polar
%   Newton-Raphson load flow is transmission-oriented but converges on this small case.

%% MATPOWER Case Format : Version 2
mpc.version = '2';

%%-----  Power Flow Data  -----%%
%% system MVA base
mpc.baseMVA = 100;

%% bus data
%	bus_i	type	Pd	Qd	Gs	Bs	area	Vm	Va	baseKV	zone	Vmax	Vmin
mpc.bus = [
	0	3	0	0	0	0	1	1.03	0	110	1	1.1	0.9;
	1	1	19.839	4.6372	0	0	1	1	0	20	1	1.1	0.9;
	2	1	0	0	0	0	1	1	0	20	1	1.1	0.9;
	3	1	0.5017	0.2089	0	0	1	1	0	20	1	1.1	0.9;
	4	1	0.43165	0.10818	0	0	1	1	0	20	1	1.1	0.9;
	5	1	0.7275	0.18233	0	0	1	1	0	20	1	1.1	0.9;
	6	1	0.54805	0.13735	0	0	1	1	0	20	1	1.1	0.9;
	7	1	0.0765	0.04741	0	0	1	1	0	20	1	1.1	0.9;
	8	1	0.58685	0.14708	0	0	1	1	0	20	1	1.1	0.9;
	9	1	0.57375	0.35558	0	0	1	1	0	20	1	1.1	0.9;
	10	1	0.5433	0.16126	0	0	1	1	0	20	1	1.1	0.9;
	11	1	0.3298	0.08266	0	0	1	1	0	20	1	1.1	0.9;
	12	1	20.010	4.6934	0	0	1	1	0	20	1	1.1	0.9;
	13	1	0.034	0.02107	0	0	1	1	0	20	1	1.1	0.9;
	14	1	0.54005	0.25771	0	0	1	1	0	20	1	1.1	0.9;
];

%% generator data
%	bus	Pg	Qg	Qmax	Qmin	Vg	mBase	status	Pmax	Pmin
mpc.gen = [
	0	0	0	9999	-9999	1.03	100	1	9999	-9999;
];

%% branch data
%	fbus	tbus	r	x	b	rateA	rateB	rateC	ratio	angle	status	angmin	angmax
mpc.branch = [
	1	2	0.353205	0.504780	0.000535675	5	5	5	0	0	1	-360	360;
	2	3	0.553605	0.791180	0.000839604	5	5	5	0	0	1	-360	360;
	3	4	0.0764025	0.109190	0.000115873	5	5	5	0	0	1	-360	360;
	4	5	0.070140	0.100240	0.000106375	5	5	5	0	0	1	-360	360;
	5	6	0.192885	0.275660	0.000292532	5	5	5	0	0	1	-360	360;
	7	8	0.2091675	0.298930	0.000317226	5	5	5	0	0	1	-360	360;
	8	9	0.040080	0.057280	0.0000607858	5	5	5	0	0	1	-360	360;
	9	10	0.0964425	0.137830	0.000146266	5	5	5	0	0	1	-360	360;
	10	11	0.0413325	0.059070	0.0000626853	5	5	5	0	0	1	-360	360;
	3	8	0.162825	0.232700	0.000246942	5	5	5	0	0	1	-360	360;
	12	13	0.623475	0.447435	0.0000620445	7	7	7	0	0	1	-360	360;
	13	14	0.381225	0.273585	0.0000379372	7	7	7	0	0	1	-360	360;
	0	1	0.0064	0.48	0	25	25	25	1	30	1	-360	360;
	0	12	0.0064	0.48	0	25	25	25	1	30	1	-360	360;
];
