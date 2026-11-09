#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Bounds {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

pub(crate) const MAX_WINDOWS: usize = 16;

pub(crate) fn plan_tiles(work: Bounds, gap: i32, minimums: &[(i32, i32)]) -> Option<Vec<Bounds>> {
    let count = minimums.len();
    if count == 0
        || count > MAX_WINDOWS
        || work.width <= 0
        || work.height <= 0
        || work.x.checked_add(work.width).is_none()
        || work.y.checked_add(work.height).is_none()
        || gap < 0
        || minimums
            .iter()
            .any(|&(width, height)| width <= 0 || height <= 0)
    {
        return None;
    }

    // Prefer balanced rectangular cells. The grid uses the monitor's entire work area,
    // with an empty last cell when the best fitting grid has more cells than windows.
    let mut best = None;
    for columns in 1..=count {
        let rows = count.div_ceil(columns);
        let usable_width = i64::from(work.width) - i64::from(gap) * (columns as i64 - 1);
        let usable_height = i64::from(work.height) - i64::from(gap) * (rows as i64 - 1);
        let cell_width = usable_width / columns as i64;
        let cell_height = usable_height / rows as i64;
        if cell_width <= 0 || cell_height <= 0 {
            continue;
        }

        let aspect = cell_width as f64 / cell_height as f64;
        let score = (aspect / (4.0 / 3.0)).ln().abs() + (columns * rows - count) as f64 * 0.1;
        if best.as_ref().is_some_and(|(current, _)| score >= *current) {
            continue;
        }
        let cells: Vec<_> = (0..count)
            .map(|index| {
                let col = (index % columns) as i64;
                let row = (index / columns) as i64;
                let left = col * usable_width / columns as i64;
                let top = row * usable_height / rows as i64;
                Bounds {
                    x: work.x + (left + col * i64::from(gap)) as i32,
                    y: work.y + (top + row * i64::from(gap)) as i32,
                    width: ((col + 1) * usable_width / columns as i64 - left) as i32,
                    height: ((row + 1) * usable_height / rows as i64 - top) as i32,
                }
            })
            .collect();
        if cells
            .iter()
            .zip(minimums)
            .all(|(cell, &(width, height))| cell.width >= width && cell.height >= height)
        {
            best = Some((score, cells));
        }
    }
    best.map(|(_, cells)| cells)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn negative_monitor_coordinates_and_odd_pixels_are_covered_evenly() {
        let work = Bounds {
            x: -1921,
            y: -23,
            width: 1921,
            height: 1041,
        };
        let result = plan_tiles(work, 8, &[(400, 320); 2]).unwrap();
        assert_eq!(
            result,
            [
                Bounds {
                    x: -1921,
                    y: -23,
                    width: 956,
                    height: 1041
                },
                Bounds {
                    x: -957,
                    y: -23,
                    width: 957,
                    height: 1041
                },
            ]
        );
    }

    #[test]
    fn tall_monitors_stack_and_impossible_minimums_reject_the_entire_layout() {
        let work = Bounds {
            x: 0,
            y: 30,
            width: 800,
            height: 1260,
        };
        let result = plan_tiles(work, 8, &[(480, 500); 2]).unwrap();
        assert_eq!(result[1].y, 664);
        assert_eq!(result[1].height, 626);
        assert!(plan_tiles(work, 8, &[(480, 640); 2]).is_none());
    }

    #[test]
    fn sixteen_tiles_fit_inside_work_area_and_never_overlap() {
        let work = Bounds {
            x: -500,
            y: 170,
            width: 3840,
            height: 2100,
        };
        let result = plan_tiles(work, 12, &[(640, 360); 16]).unwrap();
        for (index, a) in result.iter().enumerate() {
            assert!(a.x >= work.x && a.y >= work.y);
            assert!(a.x + a.width <= work.x + work.width && a.y + a.height <= work.y + work.height);
            for b in &result[index + 1..] {
                assert!(
                    a.x + a.width <= b.x
                        || b.x + b.width <= a.x
                        || a.y + a.height <= b.y
                        || b.y + b.height <= a.y
                );
            }
        }
    }
}
