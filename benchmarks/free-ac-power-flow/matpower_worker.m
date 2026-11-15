args = argv();
addpath(args{1});
install_matpower(1, 0, 0);
mpc = loadcase(args{2});
[PQ, PV, REF, NONE, BUS_I, BUS_TYPE, PD, QD, GS, BS, BUS_AREA, VM, VA] = idx_bus();
mpc.bus(:, VA) = 0;
mpc.bus(mpc.bus(:, BUS_TYPE) == PQ, VM) = 1;
options = mpoption('model', 'AC', 'pf.alg', 'NR', 'pf.tol', 1e-8, ...
                   'pf.nr.max_it', 50, 'pf.enforce_q_lims', 0, ...
                   'verbose', 0, 'out.all', 0);
runpf(mpc, options);
for attempt = 1:10
    started = tic();
    [result, success] = runpf(mpc, options);
    elapsed = 1000 * toc(started);
    row = struct('attempt', attempt, 'elapsedMs', elapsed, ...
                 'converged', logical(success), 'iterations', result.iterations, ...
                 'version', mpver(), 'runtime', version(), ...
                 'bus', result.bus(:, [BUS_I VM VA]), 'branch', result.branch(:, 14:17));
    file = fopen(fullfile(args{3}, sprintf('attempt-%02d.json', attempt)), 'w');
    if file < 0
        error('Could not open attempt file');
    end
    fprintf(file, '%s\n', jsonencode(row));
    fclose(file);
end
