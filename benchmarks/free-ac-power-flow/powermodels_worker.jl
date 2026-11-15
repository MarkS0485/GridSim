using PowerModels
using Printf

case_path, output = ARGS
base_grid = PowerModels.parse_file(case_path)
for bus in values(base_grid["bus"])
    bus["va"] = 0.0
    if bus["bus_type"] == 1
        bus["vm"] = 1.0
    end
    bus["va_start"] = bus["va"]
    bus["vm_start"] = bus["vm"]
end

solve(grid) = PowerModels.compute_ac_pf(grid; ftol=1e-8, xtol=0.0, iterations=50)
solve(deepcopy(base_grid))
records = []
for attempt in 1:10
    grid = deepcopy(base_grid)
    started = time_ns()
    result = solve(grid)
    ms = (time_ns() - started) / 1e6
    push!(records, (attempt, ms, result))
end
for (attempt, ms, result) in records
    open(joinpath(output, @sprintf("attempt-%02d.json", attempt)), "w") do io
        ok = result["termination_status"] == true
        print(io, "{\"attempt\":", attempt, ",\"elapsedMs\":", ms,
              ",\"converged\":", ok, ",\"version\":\"", pkgversion(PowerModels),
              "\",\"iterations\":null,\"bus\":[")
        if ok
            rows = ["[$(parse(Int, id)),$(state["vm"]),$(rad2deg(state["va"]))]"
                    for (id, state) in result["solution"]["bus"] if haskey(state, "vm")]
            print(io, join(rows, ","))
        end
        println(io, "]}")
    end
end
