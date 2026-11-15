args = argv();
addpath(args{1});
install_matpower(1, 0, 0);
for k = 3:numel(args)
    name = args{k};
    try
        mpc = loadcase(fullfile(args{1}, 'data', name));
        for field = {'dcline', 'switch', 'buslink'}
            if isfield(mpc, field{1}) && ~isempty(mpc.(field{1}))
                error('Case has unsupported electrical field %s', field{1});
            end
        end
        [~, stem] = fileparts(name);
        file = fopen(fullfile(args{2}, name), 'w');
        if file < 0
            error('Could not open materialised case');
        end
        cleanup = onCleanup(@() fclose(file));
        fprintf(file, 'function mpc = %s\nmpc.version = ''2'';\n', stem);
        fprintf(file, 'mpc.baseMVA = %.17g;\n', mpc.baseMVA);
        fields = {'bus', 'gen', 'branch'};
        widths = [13, 21, 13];
        for j = 1:numel(fields)
            fprintf(file, 'mpc.%s = [\n', fields{j});
            matrix = mpc.(fields{j});
            width = min(size(matrix, 2), widths(j));
            fprintf(file, [repmat(' %.17g', 1, width), ';\n'], matrix(:, 1:width).');
            fprintf(file, '];\n');
        end
        clear cleanup;
    catch err
        fprintf(2, '%s: %s\n', name, err.message);
    end
end
